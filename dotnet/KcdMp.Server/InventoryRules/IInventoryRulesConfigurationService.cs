using KcdMp.Server.Respawn;

namespace KcdMp.Server.InventoryRules;

public interface IInventoryRulesConfigurationService
{
    Task<InventoryRulesConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default);

    Task<CharacterInventoryRuleStateRecord?> GetCharacterStateAsync(
        string characterId,
        CancellationToken ct = default);

    Task<InventoryRuleApplyResult> ApplyCharacterDefeatStateAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CharacterDefeatState defeatState,
        InventoryRuleTrigger trigger,
        CancellationToken ct = default);

    Task<InventoryContainerAccessEvaluationResult> EvaluateContainerAccessAsync(
        InventoryContainerAccessEvaluationRequest request,
        CancellationToken ct = default);
}

public sealed class NoOpInventoryRulesConfigurationService : IInventoryRulesConfigurationService
{
    public Task<InventoryRulesConfigurationRecord> GetActiveConfigurationAsync(CancellationToken ct = default)
        => Task.FromResult(new InventoryRulesConfigurationRecord());

    public Task<CharacterInventoryRuleStateRecord?> GetCharacterStateAsync(
        string characterId,
        CancellationToken ct = default)
        => Task.FromResult<CharacterInventoryRuleStateRecord?>(null);

    public Task<InventoryRuleApplyResult> ApplyCharacterDefeatStateAsync(
        Guid sessionId,
        string identityId,
        string characterId,
        CharacterDefeatState defeatState,
        InventoryRuleTrigger trigger,
        CancellationToken ct = default)
    {
        var state = new CharacterInventoryRuleStateRecord
        {
            CharacterId = characterId,
            LastDefeatState = defeatState,
            LastTrigger = trigger,
            IsLootable = defeatState == CharacterDefeatState.Unconscious,
            State = defeatState == CharacterDefeatState.Unconscious
                ? InventoryCharacterRuleState.Lootable
                : InventoryCharacterRuleState.Normal,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastSavedAtUtc = DateTimeOffset.UtcNow,
        };
        return Task.FromResult(new InventoryRuleApplyResult(true, null, state, Changed: true));
    }

    public Task<InventoryContainerAccessEvaluationResult> EvaluateContainerAccessAsync(
        InventoryContainerAccessEvaluationRequest request,
        CancellationToken ct = default)
    {
        var allowed = request.HasValidKey || request.IsFullyOpen || request.LockpickSucceeded;
        var state = request.HasValidKey
            ? InventoryContainerAccessState.KeyAuthorized
            : request.IsFullyOpen
                ? InventoryContainerAccessState.OpenAuthorized
                : request.LockpickSucceeded
                    ? InventoryContainerAccessState.LockpickAuthorized
                    : InventoryContainerAccessState.Denied;
        return Task.FromResult(new InventoryContainerAccessEvaluationResult(
            allowed,
            state,
            request.ContainerTypeId,
            allowed ? null : "Container access denied."));
    }
}
