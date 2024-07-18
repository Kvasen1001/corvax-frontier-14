using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server.GameTicking;
using Content.Shared.Bank.Components;
using Content.Shared.Preferences;
using Robust.Shared.GameStates;
using Robust.Shared.Network;
using Content.Shared._NF.Bank.Events;
using Robust.Server.Player;
using Content.Shared.Preferences.Loadouts;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Content.Shared.Database;

namespace Content.Server.Bank;

public sealed partial class BankSystem : EntitySystem
{
    [Dependency] private readonly IServerPreferencesManager _prefsManager = default!;
    [Dependency] private readonly IServerDbManager _dbManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;

    [Dependency] private readonly IDependencyCollection _dependencies = default!; // Corvax-Frontier-Changes

    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("bank");
        SubscribeLocalEvent<BankAccountComponent, ComponentGetState>(OnBankAccountChanged);
        SubscribeLocalEvent<PlayerJoinedLobbyEvent>(OnPlayerLobbyJoin);
        InitializeATM();
        InitializeStationATM();
    }

    // To ensure that bank account data gets saved, we are going to update the db every time the component changes
    // I at first wanted to try to reduce database calls, however notafet suggested I just do it every time the account changes
    // TODO: stop it from running 5 times every time
    private void OnBankAccountChanged(EntityUid mobUid, BankAccountComponent bank, ref ComponentGetState args)
    {
        if (args.Player is null)
            return;

        var user = args.Player?.UserId;

        if (user == null || args.Player?.AttachedEntity != mobUid)
        {
            return;
        }

        var prefs = _prefsManager.GetPreferences((NetUserId) user);
        var character = prefs.SelectedCharacter;
        var index = prefs.IndexOfCharacter(character);

        if (character is not HumanoidCharacterProfile profile)
        {
            return;
        }

        var newProfile = new HumanoidCharacterProfile(
            profile.Name,
            profile.FlavorText,
            profile.Species,
            profile.Age,
            profile.Sex,
            profile.Gender,
            bank.Balance,
            profile.Appearance,
            profile.SpawnPriority,
            new Dictionary<ProtoId<JobPrototype>, JobPriority>(profile.JobPriorities), // Frontier Merge
            profile.PreferenceUnavailable,
            new HashSet<ProtoId<AntagPrototype>>(profile.AntagPreferences), // Frontier Merge
            new HashSet<ProtoId<TraitPrototype>>(profile.TraitPreferences), // Frontier Merge
            new Dictionary<string, RoleLoadout>(profile.Loadouts));

        newProfile.EnsureValid(args.Player, _dependencies, []);

        args.State = new BankAccountComponentState
        {
            Balance = bank.Balance,
        };
        // idk if it works as i want
        Task.Run(() => SaveCharSlot((NetUserId) user, profile, index)).Wait(300); // Corvax-Frontier-Changes
        _log.Info($"Character {profile.Name} saved");
    }
    // Corvax-Frontier-Changes-Start
    private async Task SaveCharSlot(NetUserId userId, HumanoidCharacterProfile profile, int index)
    {
        try
        {
            await _dbManager.SaveCharacterSlotAsync(userId, profile, index);
        }
        catch (Exception ex)
        {
            _log.Error($"Caught exception while saving bank account to database: {ex.Message}. Stack Trace: {ex.StackTrace}");
        }
    }
    // Corvax-Frontier-Changes-End
    /// <summary>
    /// Attempts to remove money from a character's bank account. This should always be used instead of attempting to modify the bankaccountcomponent directly
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is attached to, typically the player controlled mob</param>
    /// <param name="amount">The integer amount of which to decrease the bank account</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public bool TryBankWithdraw(EntityUid mobUid, int amount)
    {
        if (amount <= 0)
        {
            _log.Info($"{amount} is invalid");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
        {
            _log.Info($"{mobUid} has no bank account");
            return false;
        }

        if (bank.Balance < amount)
        {
            _log.Info($"{mobUid} has insufficient funds");
            return false;
        }
        _adminLogger.Add(LogType.Balance, LogImpact.Medium, $"Balance change from {ToPrettyString(mobUid)}, old balance: {bank.Balance}, withdrew: {amount}");
        bank.Balance -= amount;
        _log.Info($"{mobUid} withdrew {amount}");
        if (_playerManager.TryGetSessionByEntity(mobUid, out var player))
        {
            RaiseLocalEvent(new BalanceChangedEvent(bank.Balance, player));
        }
        Dirty(bank);
        return true;
    }

    /// <summary>
    /// Attempts to add money to a character's bank account. This should always be used instead of attempting to modify the bankaccountcomponent directly
    /// </summary>
    /// <param name="mobUid">The UID that the bank account is connected to, typically the player controlled mob</param>
    /// <param name="amount">The integer amount of which to increase the bank account</param>
    /// <returns>true if the transaction was successful, false if it was not</returns>
    public bool TryBankDeposit(EntityUid mobUid, int amount)
    {
        if (amount <= 0)
        {
            _log.Info($"{amount} is invalid");
            return false;
        }

        if (!TryComp<BankAccountComponent>(mobUid, out var bank))
        {
            _log.Info($"{mobUid} has no bank account");
            return false;
        }

        bank.Balance += amount;
        _log.Info($"{mobUid} deposited {amount}");
        Dirty(bank);
        return true;
    }

    /// <summary>
    /// ok so this is incredibly fucking cursed, and really shouldnt be calling LoadData
    /// However
    /// as of writing, the preferences system caches all player character data at the time of client connection.
    /// This is causing some bad bahavior where the cache is becoming outdated after character data is getting saved to the db
    /// and there is no method right now to invalidate and refresh this cache to ensure we get accurate bank data from the database,
    /// resulting in respawns/round restarts populating the bank account component with an outdated cache and then re-saving that
    /// bad cached data into the db.
    /// effectively a gigantic money exploit.
    /// So, this will have to stay cursed until I can find another way to refresh the character cache
    /// or the db gods themselves come up to smite me from below, whichever comes first
    ///
    /// EDIT 5/13/2024 THE DB GODS THEY CAME. THEY SMOTE. SAVE ME
    /// </summary>
    private void OnPlayerLobbyJoin (PlayerJoinedLobbyEvent args)
    {
        var cts = new CancellationToken();
        _prefsManager.RefreshPreferencesAsync(args.PlayerSession, cts);
    }
}
