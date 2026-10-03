using System;
using System.Linq;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using CasinoExpansion.Core;
using Controller = Il2CppScheduleOne.Casino.CasinoGameController;

namespace CasinoExpansion.Casino
{
    // Runs a round for a mod game at a table whose vanilla deal is suppressed. Plain C#, ticked
    // from Mod.OnUpdate -- a custom MonoBehaviour would need IL2CPP type registration, and a
    // NetworkBehaviour cannot be created at all.
    public sealed class TableSession
    {
        private static readonly Dictionary<int, TableSession> Sessions = new Dictionary<int, TableSession>();

        private readonly Controller _controller;
        private readonly string _gameId;
        private int _round;
        private bool _dealing;
        private bool _dealtThisReady;
        private bool _cancelRequested;

        public string LastResult { get; private set; } = "";
        public float Stake { get; set; } = 10f;

        // Which side this player is backing. Local to each client on purpose: the cards are
        // shared but the wager is not, so two players at one table can back opposite sides.
        public int Side { get; set; }

        // The table's own bet slider is the buy-in. Reading it rather than calling
        // SetLocalPlayerBet keeps us out of a fight with the panel over the value -- vanilla
        // rewrites it every frame from its own state, so anything we wrote would be stomped.
        private float BuyIn(ITableGame game)
        {
            // Stake is written by the slider patch and is the only figure that survives
            // vanilla's clamp to the old $1,000 table maximum. LocalPlayerBet is the fallback
            // for a table whose slider we never saw move.
            if (Stake > 0f) return Stake;

            try
            {
                float bet = _controller.LocalPlayerBet;
                if (bet > 0f) return bet;
            }
            catch { }
            return 10f;
        }

        private TableSession(Controller controller)
        {
            _controller = controller;
            _gameId = $"table{controller.GetInstanceID()}";
        }

        public static TableSession For(Controller controller)
        {
            if (controller == null) return null;
            int id = controller.GetInstanceID();
            if (!Sessions.TryGetValue(id, out var session))
                Sessions[id] = session = new TableSession(controller);
            return session;
        }
        public static void CancelFor(Controller controller)
        {
            if (controller == null) return;

            if (Sessions.TryGetValue(controller.GetInstanceID(), out var session))
                session.CancelFromClose();
        }

        private void CancelFromClose()
        {
            _cancelRequested = true;
            
        }

        public static void TickAll()
        {
            foreach (var session in Sessions.Values) session.Tick();
        }

        private void Tick()
        {
            if (_dealing || _controller == null || !_controller.IsOpen) return;

            var id = TableModes.Get(_controller);
            var game = TableGames.For(id);
            if (game == null) return;                       // vanilla, or a game not yet written

            // One round per ready-up. Vanilla leaves the ready flag set after a hand resolves,
            // so without this the next tick deals again immediately -- an endless loop taking a
            // stake every couple of seconds.
            if (!AllReady())
            {
                _dealtThisReady = false;
                return;
            }

            if (_dealtThisReady || !Consensus(id)) return;

            _dealtThisReady = true;
            MelonCoroutines.Start(RunRound(game));
        }

        private bool AllReady()
        {
            var bj = _controller.TryCast<Il2CppScheduleOne.Casino.BlackjackGameController>();
            if (bj != null) return bj.AreAllPlayersReady();

            var rtb = _controller.TryCast<Il2CppScheduleOne.Casino.RTBGameController>();
            return rtb != null && rtb.AreAllPlayersReady();
        }

        // Every seated player must have chosen the same game. The selection replicates through
        // SetData/SendPlayerFloat, so this is correct in multiplayer as well as solo -- where
        // there is one seat and it is trivially true.
        private bool Consensus(ETableGame local)
        {
            try
            {
                var players = _controller.Players;
                if (players == null) return true;

                for (int i = 0; i < players.CurrentPlayerCount; i++)
                {
                    var data = players.GetPlayerData(i);
                    if (data == null) continue;

                    float choice = data.GetData<float>(Keys.Game(_gameId));
                    if ((int)choice != (int)local) return false;
                }
                return true;
            }
            catch { return true; }   // never block a round on a replication hiccup
        }

        public void PublishChoice(ETableGame game)
        {
            try
            {
                _controller.LocalPlayerData?.SetData<float>(Keys.Game(_gameId), (float)(int)game, true);
            }
            catch (Exception e) { MelonLogger.Warning($"[session] could not publish choice: {e.Message}"); }
        }

        private System.Collections.IEnumerator RunRound(ITableGame game)
        {
            _dealing = true;
            _cancelRequested = false;
            _round++;

            float stake = Mathf.Clamp(BuyIn(game), game.Limits.Min, game.Limits.Max);

            if (!Bank.TryTakeBet(_gameId, _round, stake))
            {
                Bank.TryGetCashBalance(out var bal);
                LastResult = $"Not enough cash: need ${stake:N0}, have ${bal:N0}";
                MelonLogger.Msg($"[session] {LastResult}");

                try
                {
                    if (AllReady())
                        _controller.ToggleLocalPlayerReady();
                }
                catch { }

                _dealing = false;
                yield break;
            }

            if (game.Sides.Length > 0)
            {
                yield return MelonCoroutines.Start(Ask(
                    $"${stake:N0} on which side?",
                    game.Sides,
                    i => Side = i,
                    20f,
                    0));

                if (_cancelRequested)
                    yield break;
            }

            int seed = UnityEngine.Random.Range(1, RoundState.MaxExactInt);

            var deck = new Deck(seed, game.Decks);
            var hands = new HandSet();

            hands.Notes["side"] = Side;
            hands.Notes["game"] = (float)(int)game.Id;

            game.Deal(hands, deck);

            LastResult = $"<b>{game.Title}</b>\n${stake:N0} staked\nDealing...";

            _placed = null;

            if (TableCards.Supported(_controller))
            {
                TableCards.Clear(_controller);
                TableCards.BeginRound(_controller);
            }

            yield return MelonCoroutines.Start(Show(hands));

            if (_cancelRequested)
                yield break;

            LastResult = Describe(game, hands);

            var wager = new Wager(stake);

            if (game is IDecidingGame deciding)
            {
                yield return MelonCoroutines.Start(
                    deciding.Decide(this, hands, deck, wager));

                if (_cancelRequested)
                    yield break;

                yield return MelonCoroutines.Start(Show(hands));

                if (_cancelRequested)
                    yield break;

                LastResult = Describe(game, hands);
            }

            if (TableCards.Supported(_controller))
                yield return MelonCoroutines.Start(
                    TableCards.Reveal(_controller));

            if (_cancelRequested)
                yield break;

            LastResult = Describe(game, hands);

            yield return new WaitForSeconds(1.2f);

            if (_cancelRequested)
                yield break;

            var outcome = game.Resolve(hands, wager, Side);

            if (outcome.Multiplier > 0f)
                Bank.ApplyPayout(
                    _gameId,
                    _round,
                    wager.Total * outcome.Multiplier);

            float won = wager.Total * outcome.Multiplier;

            LastResult =
                outcome.Multiplier > 1f
                    ? $"<b>WON ${won - wager.Total:N0}</b>\n{outcome.Summary}"
                : outcome.Multiplier > 0f
                    ? $"<b>Push</b>\n{outcome.Summary}"
                : $"<b>Lost ${wager.Total:N0}</b>\n{outcome.Summary}";

            MelonLogger.Msg(
                $"[session] round {_round} seed {seed} staked {wager.Total}: " +
                $"{outcome.Summary} -> x{outcome.Multiplier}");

            yield return new WaitForSeconds(1f);

            if (_cancelRequested)
                yield break;

            TableInterface.Finish();

            if (TableCards.Supported(_controller))
                TableCards.EndRound(_controller);

            if (AllReady())
            {
                try
                {
                    _controller.ToggleLocalPlayerReady();
                }
                catch (Exception e)
                {
                    MelonLogger.Warning(
                        $"[session] could not clear ready: {e.Message}");
                }
            }

            _dealtThisReady = false;
            _dealing = false;
        }

        private static string Describe(ITableGame game, HandSet hands)
        {
            var text = new System.Text.StringBuilder($"<b>{game.Title}</b>\n");
            foreach (var hand in hands.Hands) text.AppendLine($"{hand.Name}: {hand}");
            return text.ToString();
        }

        public bool HasFreshResult => !string.IsNullOrEmpty(LastResult);

        // ---- mid-round decisions -------------------------------------------------------
        //
        // The panel polls these rather than being pushed to, matching how the rest of the
        // chrome refreshes. Only the local player answers: a decision changes this client's
        // wager, never the cards, so the two clients still resolve identical hands.

        public string Prompt { get; private set; }
        public string[] Options { get; private set; }
        private int _answer = -1;

        public bool Waiting => Options != null;

        // True while the prompt is being shown on the table itself, so the bet panel does not
        // draw a second copy of the same buttons beside it.
        public bool PromptOnTable { get; private set; }

        public void Answer(int index)
        {
            if (Options != null && index >= 0 && index < Options.Length) _answer = index;
        }

        // An unanswered prompt falls through to `fallback`, which defaults to option 1 -- the
        // passive choice in every game here: Stand, Fold, Cash out. It used to fall through to
        // the LAST option, which in blackjack is Double or Split, so a prompt nobody could see
        // answered itself by raising the stake and hitting again.
        public System.Collections.IEnumerator Ask(string prompt, string[] options, Action<int> chosen,
                                                  float timeout = 25f, int fallback = 1)
        {
            Prompt = prompt;
            Options = options;
            _answer = -1;

            // Shown on the table's own input panel where one exists, because the bet panel --
            // where the rest of the chrome lives -- is closed while a hand is in play.
            TableInterface.Active = this;
            bool onTable = TableInterface.Show(prompt, options);
            PromptOnTable = onTable;

            float deadline = Time.unscaledTime + timeout;
            while (!_cancelRequested && _answer < 0 && Time.unscaledTime < deadline)
                yield return null;

            if (_cancelRequested)
            {
                Prompt = null;
                Options = null;
                PromptOnTable = false;
                if (onTable) TableInterface.Hide();
                yield break;
            }

            int pick = _answer >= 0 ? _answer : Mathf.Clamp(fallback, 0, options.Length - 1);
            Prompt = null;
            Options = null;

            PromptOnTable = false;
            if (onTable) TableInterface.Hide();

            chosen?.Invoke(pick);
        }

        public void Announce(string text) => LastResult = text;

        // The Dealer/You readout on the table's own interface. Games set it because only they
        // know what a score means -- 21 in blackjack, nine in baccarat, a spread in Red Dog.
        public void Scores(string dealer, string player) => TableInterface.Scores(dealer, player);

        // Deals whatever is in the hands but not yet on the felt. Games call this after adding
        // cards mid-round -- a blackjack hit has to land before the next prompt, not after the
        // whole decision sequence is over.
        private int[] _placed;

        // Clears the felt and deals the whole set again. Needed when a decision rearranges
        // cards that are already down -- setting a pai gow hand moves two of the seven into a
        // second row, and there is no way to slide individual cards about after the fact.
        public System.Collections.IEnumerator Relayout(HandSet hands)
        {
            if (!TableCards.Supported(_controller))
            {
                yield return new WaitForSeconds(0.3f);
                yield break;
            }

            TableCards.Clear(_controller);
            _placed = null;
            yield return MelonCoroutines.Start(Show(hands));
        }

        public System.Collections.IEnumerator Show(HandSet hands)
        {
            if (!TableCards.Supported(_controller))
            {
                yield return new WaitForSeconds(0.5f);
                yield break;
            }

            var from = new int[hands.Hands.Count];
            if (_placed != null)
                for (int i = 0; i < from.Length && i < _placed.Length; i++) from[i] = _placed[i];

            yield return MelonCoroutines.Start(TableCards.DealOut(_controller, hands, 0.32f, from));

            _placed = hands.Hands.Select(h => h.Cards.Count).ToArray();
        }

        public System.Collections.IEnumerator Wait(float seconds)
        {
            yield return new WaitForSeconds(seconds);
        }

        public bool TakeRaise(float amount) => Bank.TryTakeRaise(_gameId, _round, amount);
    }
}
