namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>Keys in AdditionalProperties for hosted execution and durable provider history.</summary>
public static class AnthropicCodeExecutionMetadata
{
    public const string Hosting = "anthropic_hosting";
    public const string ProviderScope = "anthropic_provider_scope";
    public const string Provider = "anthropic_provider";
    public const string ContainerId = "anthropic_container_id";
    public const string ContainerExpiresAt = "anthropic_container_expires_at";
    public const string StopReason = "anthropic_stop_reason";
    public const string ExecutionState = "anthropic_execution_state";
    public const string ExecutionProgress = "anthropic_execution_progress";
    public const string Operation = "anthropic_operation";
    public const string Input = "anthropic_input";
    public const string Outcome = "anthropic_outcome";
    public const string ReturnCode = "anthropic_return_code";
    public const string ErrorCode = "anthropic_error_code";
    public const string OutputChannel = "anthropic_output_channel";
    public const string Stdout = "anthropic_stdout";
    public const string Stderr = "anthropic_stderr";
    public const string RawContentBlock = "anthropic_raw_content_block";
    public const string ReplayBlocks = "anthropic_replay_blocks";
}
