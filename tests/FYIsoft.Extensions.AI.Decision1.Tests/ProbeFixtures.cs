// Generated from the raw HTTP probe of a real Microsoft-Decision-1 deployment on 2026-10-18
// (decision-1-test, East US 2). Bodies and headers are verbatim; the probe captured no credentials.
// Fixtures named Synthetic* are hand-written in the same documented error shape.
namespace Microsoft.Extensions.AI.Decision1.Tests;

internal sealed record ProbeFixture(int Status, string Body, IReadOnlyDictionary<string, string> Headers);

internal static class ProbeFixtures
{
    /// <summary>Probe case "combined-entra-services" (HTTP 200).</summary>
    public static readonly ProbeFixture CombinedEntraServices = new(200,
        """{"model":"microsoft-decision-1","answers":{"refund":{"noul":0.0019267495720550704,"type":"noul"},"repeat_contact":{"noul":0.9953904259439476,"type":"noul"},"severity":{"confidence":0.6903605937390916,"legend":{"0":"Cosmetic","1":"Minor","2":"Major","3":"Critical"},"probabilities":{"0":0.0013079888902714876,"1":0.010951656893852506,"2":0.7677704453043187,"3":0.2199699089115573},"score":2.2064022742371616,"type":"score"},"team":{"choice":"engineering","confidence":0.9874681282751889,"probabilities":{"billing":0.0024580428604436297,"engineering":0.9916454188501259,"support":0.005896538289430438},"type":"choice"}},"usage":{"input_tokens":155,"output_tokens":4}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "5dbe01c2-a4cd-4a4d-b4e5-32dff2563068", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "29", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "2", ["x-request-id"] = "139b6fcb-42f3-4dfc-87de-90007ebfc9f3" });

    /// <summary>Probe case "string-state-noul-apikey" (HTTP 200).</summary>
    public static readonly ProbeFixture StringStateNoulApikey = new(200,
        """{"model":"microsoft-decision-1","answers":{"billing":{"noul":0.9980732633607365,"type":"noul"}},"usage":{"input_tokens":28,"output_tokens":1}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "6fc59964-006e-458d-abb2-1146bad578e7", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "27", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "5", ["x-request-id"] = "1cd1e02c-5ebd-486e-80ae-13359d1fc0c9" });

    /// <summary>Probe case "score-10-levels" (HTTP 200).</summary>
    public static readonly ProbeFixture Score10Levels = new(200,
        """{"model":"microsoft-decision-1","answers":{"materiality":{"confidence":0.6718172137542939,"legend":{"0":"1","1":"2","2":"3","3":"4","4":"5","5":"6","6":"7","7":"8","8":"9","9":"10"},"probabilities":{"0":0.7046354923788646,"1":0.1387509510775537,"2":0.016571441887961797,"3":0.02127814750220054,"4":0.006908004734400788,"5":0.0060962927811484165,"6":0.01138937434370377,"7":0.010051087580695036,"8":0.018777899263430122,"9":0.06554130845004122},"score":1.172630589488324,"type":"score"}},"usage":{"input_tokens":55,"output_tokens":1}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "4b53d3c6-cd85-46a0-8fd5-7a8d447642e5", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "26", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "7", ["x-request-id"] = "4f75dceb-08f7-4cfc-9ec8-2084fd5fd618" });

    /// <summary>Probe case "score-11-levels" (HTTP 400).</summary>
    public static readonly ProbeFixture Score11Levels = new(400,
        """{"error":{"code":"unsupported_request_argument","message":"Model does not support request argument supplied: score question s requires 2-10 levels","details":"Model does not support request argument supplied: score question s requires 2-10 levels"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "b68688b4-9eb0-4cb8-9317-d904380fc45d", ["x-ms-error-code"] = "unsupported_request_argument", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "25", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "9" });

    /// <summary>Probe case "choice-array-criteria" (HTTP 400).</summary>
    public static readonly ProbeFixture ChoiceArrayCriteria = new(400,
        """{"error":{"code":"unsupported_request_argument","message":"Model does not support request argument supplied: choice question kind requires 2-255 options","details":"Model does not support request argument supplied: choice question kind requires 2-255 options"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "25e88334-160a-4f0e-a2da-714fb2ac8f8b", ["x-ms-error-code"] = "unsupported_request_argument", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "25", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "10" });

    /// <summary>Probe case "bad-type-400" (HTTP 400).</summary>
    public static readonly ProbeFixture BadType400 = new(400,
        """{"error":{"code":"unsupported_request_argument","message":"Model does not support request argument supplied: invalid question q","details":"Model does not support request argument supplied: invalid question q"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "01133344-7d76-46da-a27e-d71743f310fb", ["x-ms-error-code"] = "unsupported_request_argument", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "23", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "14" });

    /// <summary>Probe case "unknown-deployment" (HTTP 404).</summary>
    public static readonly ProbeFixture UnknownDeployment = new(404,
        """{"error":{"code":"DeploymentNotFound","message":"The API deployment no-such-deployment does not exist. If you created the deployment within the last 5 minutes, please wait a moment and try again.","details":"The API deployment no-such-deployment does not exist. If you created the deployment within the last 5 minutes, please wait a moment and try again."}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "8ada8cac-627d-4340-ad77-23c221d39527", ["x-ms-error-code"] = "DeploymentNotFound", ["x-ms-region"] = "East US 2" });

    /// <summary>Probe case "no-auth-401" (HTTP 401).</summary>
    public static readonly ProbeFixture NoAuth401 = new(401,
        """{"error":{"code":"401","message":"Access denied due to invalid subscription key or wrong API endpoint. Make sure to provide a valid key for an active subscription and use a correct regional API endpoint for your resource."}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "b0a720de-7be6-4343-b491-87c9bb4b0b70" });

    /// <summary>Probe case "empty-questions" (HTTP 400).</summary>
    public static readonly ProbeFixture EmptyQuestions = new(400,
        """{"error":{"code":"unsupported_request_argument","message":"Model does not support request argument supplied: questions must be a nonempty object","details":"Model does not support request argument supplied: questions must be a nonempty object"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "887eddc3-bf25-4954-8aa9-5806d8e5e4d2", ["x-ms-error-code"] = "unsupported_request_argument", ["x-ms-region"] = "East US 2", ["x-ratelimit-limit-requests"] = "30", ["x-ratelimit-remaining-requests"] = "22", ["x-ratelimit-renewalperiod-requests"] = "60", ["x-ratelimit-reset-requests"] = "16" });

    public static readonly ProbeFixture Synthetic403 = new(403,
        """{"error":{"code":"PermissionDenied","message":"The principal lacks the required data action.","details":"Synthetic fixture"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "synthetic-403", ["x-ms-error-code"] = "PermissionDenied", ["x-ms-region"] = "East US 2" });

    public static readonly ProbeFixture Synthetic422 = new(422,
        """{"error":{"code":"unprocessable_entity","message":"The question definition is not supported.","details":{"question":"q"}}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "synthetic-422", ["x-ms-error-code"] = "unprocessable_entity", ["x-ms-region"] = "East US 2" });

    public static readonly ProbeFixture Synthetic429 = new(429,
        """{"error":{"code":"429","message":"Requests to the deployment have exceeded the rate limit.","details":"Synthetic fixture"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "synthetic-429", ["x-ms-error-code"] = "429", ["x-ms-region"] = "East US 2" });

    public static readonly ProbeFixture Synthetic500 = new(500,
        """{"error":{"code":"InternalServerError","message":"The server had an error.","details":"Synthetic fixture"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "synthetic-500", ["x-ms-error-code"] = "InternalServerError", ["x-ms-region"] = "East US 2" });

    public static readonly ProbeFixture Synthetic503 = new(503,
        """{"error":{"code":"ServiceUnavailable","message":"The service is temporarily unavailable.","details":"Synthetic fixture"}}""",
        new Dictionary<string, string> { ["apim-request-id"] = "synthetic-503", ["x-ms-error-code"] = "ServiceUnavailable", ["x-ms-region"] = "East US 2" });

}
