// SPDX-FileCopyrightText: 2024 Aiden <aiden@djkraz.com>
// SPDX-FileCopyrightText: 2024 Piras314 <p1r4s@proton.me>
// SPDX-FileCopyrightText: 2025 Aiden <28298836+Aidenkrz@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Aviu00 <93730715+Aviu00@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 Misandry <mary@thughunt.ing>
// SPDX-FileCopyrightText: 2025 SX-7 <92227810+SX-7@users.noreply.github.com>
// SPDX-FileCopyrightText: 2025 gus <august.eymann@gmail.com>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading.Tasks;
using Content.Goobstation.Common.ServerCurrency;
using Content.Server.Database;
using Robust.Server.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Network;

namespace Content.Goobstation.Server.ServerCurrency
{
    public sealed class ServerCurrencyManager : ICommonCurrencyManager
    {
        [Dependency] private readonly IServerDbManager _db = default!;
        [Dependency] private readonly ITaskManager _task = default!;
        [Dependency] private readonly IPlayerManager _player = default!;
        private readonly List<Task> _pendingSaveTasks = new();

        public event Action? ClientBalanceChange;
        public event Action<PlayerBalanceChangeEvent>? BalanceChange;
        private ISawmill _sawmill = default!;

        public void Initialize()
        {
            _sawmill = Logger.GetSawmill("server_currency");
        }

        /// <inheritdoc/>
        public void Shutdown()
        {
            _task.BlockWaitOnTask(Task.WhenAll(_pendingSaveTasks));
        }

        /// <inheritdoc/>
        public bool CanAfford(NetUserId? userId, int amount, out int balance)
        {
            balance = GetBalance(userId);
            return balance >= amount && balance - amount >= 0;
        }

        /// <inheritdoc/>
        public string Stringify(int amount) => amount == 1
            ? $"{amount} {Loc.GetString("server-currency-name-singular")}"
            : $"{amount} {Loc.GetString("server-currency-name-plural")}";

        /// <inheritdoc/>
        public int AddCurrency(NetUserId userId, int amount)
        {
            var newBalance = ModifyBalance(userId, amount);
            _sawmill.Info($"Added {amount} currency to {userId} account. Current balance: {newBalance}");
            return newBalance;
        }

        /// <inheritdoc/>
        public int RemoveCurrency(NetUserId userId, int amount)
        {
            var newBalance = ModifyBalance(userId, -amount);
            _sawmill.Info($"Removed {amount} currency from {userId} account. Current balance: {newBalance}");
            return newBalance;
        }

        /// <inheritdoc/>
        public (int, int) TransferCurrency(NetUserId sourceUserId, NetUserId targetUserId, int amount)
        {
            var newAccountValues = (ModifyBalance(sourceUserId, -amount), ModifyBalance(targetUserId, amount));
            _sawmill.Info($"Transferring {amount} currency from {sourceUserId} to {targetUserId}. Current balances: {newAccountValues.Item1}, {newAccountValues.Item2}");
            return newAccountValues;
        }

        /// <inheritdoc/>
        public int SetBalance(NetUserId userId, int amount)
        {
            var oldBalance = Task.Run(() => SetBalanceAsync(userId, amount)).GetAwaiter().GetResult();
            if (_player.TryGetSessionById(userId, out var userSession))
                BalanceChange?.Invoke(new PlayerBalanceChangeEvent(userSession, userId, amount, oldBalance));
            _sawmill.Info($"Setting {userId} account balance to {amount} from {oldBalance}");
            return oldBalance;
        }

        /// <inheritdoc/>
        public int GetBalance(NetUserId? userId = null)
        {
            return userId == null ? 0 : Task.Run(() => GetBalanceAsync(userId.Value)).GetAwaiter().GetResult();
        }

        #region Internal/Async tasks

        /// <summary>
        /// Modifies a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amountDelta">The amount of currency that will be set.</param>
        /// <returns>An integer containing the new amount of currency attributed to the player.</returns>
        /// <remarks>Use the return value instead of calling <see cref="GetBalance(NetUserId)"/> after to this.</remarks>
        private int ModifyBalance(NetUserId userId, int amountDelta)
        {
            var result = Task.Run(() => ModifyBalanceAsync(userId, amountDelta)).GetAwaiter().GetResult();
            if (_player.TryGetSessionById(userId, out var userSession))
                BalanceChange?.Invoke(new PlayerBalanceChangeEvent(userSession, userId, result, result - amountDelta));
            return result;
        }

        /// <summary>
        /// Sets a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency that will be set.</param>
        /// <param name="oldAmount">The amount of currency that will be set.</param>
        /// <remarks>This and its calees will block server shutdown until execution finishes.</remarks>
        private async Task SetBalanceAsyncInternal(NetUserId userId, int amount, int oldAmount)
        {
            var task = Task.Run(() => _db.SetServerCurrency(userId, amount));
            TrackPending(task);
            await task;
        }

        /// <summary>
        /// Sets a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency that will be set.</param>
        /// <returns>An integer containing the old amount of currency attributed to the player.</returns>
        /// <remarks>Use the return value instead of calling <see cref="GetBalance(NetUserId)"/> prior to this.</remarks>
        private async Task<int> SetBalanceAsync(NetUserId userId, int amount)
        {
            // We need to block it first to ensure we don't read our own amount, hence sync function
            var oldAmount = GetBalance(userId);
            await SetBalanceAsyncInternal(userId, amount, oldAmount);
            return oldAmount;
        }

        /// <summary>
        /// Gets a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <returns>An integer containing the amount of currency attributed to the player.</returns>
        private async Task<int> GetBalanceAsync(NetUserId userId) => await _db.GetServerCurrency(userId);

        /// <summary>
        /// Modifies a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amountDelta">The amount of currency that will be given or taken.</param>
        /// <returns>An integer containing the new amount of currency attributed to the player.</returns>
        /// <remarks>This and its calees will block server shutdown until execution finishes.</remarks>
        private async Task<int> ModifyBalanceAsync(NetUserId userId, int amountDelta)
        {
            var task = Task.Run(() => _db.ModifyServerCurrency(userId, amountDelta));
            TrackPending(task);
            return await task;
        }

        /// <summary>
        /// Track a database save task to make sure we block server shutdown on it.
        /// </summary>
        private async void TrackPending(Task task)
        {
            _pendingSaveTasks.Add(task);

            try
            {
                await task;
            }
            finally
            {
                _pendingSaveTasks.Remove(task);
            }
        }

        #endregion
    }
}
