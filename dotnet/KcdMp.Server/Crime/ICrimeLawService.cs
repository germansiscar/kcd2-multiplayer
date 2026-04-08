namespace KcdMp.Server.Crime;

public interface ICrimeLawService
{
    Task<CrimeLawConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default);

    Task<CharacterCrimeRecord?> GetCharacterRecordAsync(
        string characterId,
        CancellationToken ct = default);

    Task<CrimeRegistrationResult> RegisterAutomaticCrimeAsync(
        AutomaticCrimeRegistrationRequest request,
        CancellationToken ct = default);

    Task<CrimeRegistrationResult> RegisterManualCrimeAsync(
        ManualCrimeRegistrationRequest request,
        CancellationToken ct = default);

    Task<CrimeRegistrationResult> RegisterIllegalActionIfConfiguredAsync(
        IllegalActionCrimeRequest request,
        CancellationToken ct = default);

    Task<CrimeStateChangeResult> ClearCharacterStateAsync(
        CrimeStateClearRequest request,
        CancellationToken ct = default);
}

public sealed class NoOpCrimeLawService : ICrimeLawService
{
    public Task<CrimeLawConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default)
        => Task.FromResult(new CrimeLawConfigurationRecord());

    public Task<CharacterCrimeRecord?> GetCharacterRecordAsync(string characterId, CancellationToken ct = default)
        => Task.FromResult<CharacterCrimeRecord?>(null);

    public Task<CrimeRegistrationResult> RegisterAutomaticCrimeAsync(AutomaticCrimeRegistrationRequest request, CancellationToken ct = default)
        => Task.FromResult(new CrimeRegistrationResult(true, null, null, null, false, null));

    public Task<CrimeRegistrationResult> RegisterManualCrimeAsync(ManualCrimeRegistrationRequest request, CancellationToken ct = default)
        => Task.FromResult(new CrimeRegistrationResult(true, null, null, null, false, null));

    public Task<CrimeRegistrationResult> RegisterIllegalActionIfConfiguredAsync(IllegalActionCrimeRequest request, CancellationToken ct = default)
        => Task.FromResult(new CrimeRegistrationResult(false, "Illegal action not configured.", null, null, false, null));

    public Task<CrimeStateChangeResult> ClearCharacterStateAsync(CrimeStateClearRequest request, CancellationToken ct = default)
        => Task.FromResult(new CrimeStateChangeResult(true, null, null, false, null));
}
