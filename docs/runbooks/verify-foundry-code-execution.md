# Verify hosted code execution on the existing Foundry connection

Verified 2026-10-04: all four Anthropic live tests pass using `claude-sonnet-4-6` on the
sample's existing Foundry resource. No Azure provisioning changes are required for that route.

The resource also has `claude-sonnet-5`, but its model version is 2 (Hosted on Azure), which
rejects hosted execution. Sonnet 4.6 is version 1 (Hosted on Anthropic). This is a deployment
hosting choice, not an SDK feature toggle. See [Anthropic's hosting limitations](https://platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry#additional-features-not-supported-when-hosted-on-azure).

## Rerun using existing local credentials

From `C:\dev\SuiteFYI\AISDK`, use PowerShell. This reads the sample's existing .NET user-secret
store without printing keys, saves the current process environment, and restores it afterward.
It runs live provider calls, including uploading synthetic CSV data and downloading a generated PNG.

```powershell
$names = 'ANTHROPIC_API_KEY', 'ANTHROPIC_FOUNDRY_RESOURCE', 'ANTHROPIC_TEST_MODEL', 'ANTHROPIC_LIVE_TESTS'
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $secretPath = Join-Path $env:APPDATA 'Microsoft/UserSecrets/suitefyi-aisdk-support-desk-sample/secrets.json'
    $settings = Get-Content -Raw -LiteralPath $secretPath | ConvertFrom-Json -AsHashtable
    $env:ANTHROPIC_API_KEY = $settings['Anthropic:ApiKey']
    $env:ANTHROPIC_FOUNDRY_RESOURCE = ([uri]$settings['Anthropic:FoundryEndpoint']).Host.Split('.')[0]
    $env:ANTHROPIC_TEST_MODEL = 'claude-sonnet-4-6'
    $env:ANTHROPIC_LIVE_TESTS = '1'
    dotnet test tests/FYIsoft.Extensions.AI.Anthropic.Tests -c Release --filter 'FullyQualifiedName~AnthropicCodeExecutionLiveTests' --logger trx --results-directory artifacts/live-test-results/sonnet-4-6
    if ($LASTEXITCODE -ne 0) { throw 'Live verification failed; inspect the TRX results.' }
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
```

Expected: four passed tests. They verify ordinary chat/streaming, named function selection and
result continuation, executed calculation (338350), CSV total (60), PNG bytes and explicit
container continuation after history serialization. Results are saved beneath
`artifacts/live-test-results/sonnet-4-6`.

For application requests, pass the existing deployment name in `ChatOptions.ModelId` or the
`AnthropicChatClient` constructor, then include `HostedCodeInterpreterTool` in `ChatOptions.Tools`.
Keep ordinary chat's model selection as desired; hosted execution must use an eligible deployment.

## If you want Sonnet 5 specifically

The verified Sonnet 4.6 deployment is sufficient to run and prove the SDK feature. To use Sonnet 5
for execution, create a separate Sonnet 5 deployment with the Hosted on Anthropic option:

1. Open [Foundry](https://ai.azure.com), select the resource, and open **Discover → Models**.
2. Select Claude Sonnet 5 and choose **Deploy → Custom settings**.
3. Select **Model version 1: Hosted on Anthropic infrastructure** and **Region scope: Global**.
4. Give it a distinct deployment name, such as `claude-sonnet-5-execution`, and deploy it.
5. Use that deployment name in `ANTHROPIC_TEST_MODEL` and rerun the live tests before advertising support.

These steps follow [Microsoft's deployment guide](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-claude).
The default settings select Hosted on Azure when both options exist. Hosting on Anthropic changes
where inference runs; select it only if that fits the application's hosting requirements. No such
new deployment was created during verification.
