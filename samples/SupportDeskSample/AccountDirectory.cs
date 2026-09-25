using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace SupportDeskSample;

public sealed record Charge(DateOnly Date, decimal Amount, string Description);

public sealed record Account(
    string Name,
    string Plan,
    IReadOnlyList<Charge> RecentCharges,
    IReadOnlyList<string> OpenIncidents,
    IReadOnlyList<string> AvailablePlans);

/// <summary>
/// In-memory customer accounts, exposed to Claude as the <c>get_account</c> tool.
/// </summary>
public sealed class AccountDirectory
{
    private static readonly string[] Plans =
    [
        "Starter: $8/user/month, email support, no SSO",
        "Pro: $49/month flat for up to 5 users, priority support, no SSO",
        "Team: $15/user/month, SSO (SAML/OIDC), audit log, 99.9% SLA",
    ];

    private static readonly Dictionary<string, Account> Accounts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dana@example.com"] = new(
            "Dana Ruiz",
            "Pro",
            [
                new(new DateOnly(2026, 9, 1), 49m, "Pro subscription, September"),
                new(new DateOnly(2026, 9, 2), 49m, "Pro subscription, September (payment retry, not voided)"),
            ],
            [],
            Plans),
        ["ops@fabrikam.example"] = new(
            "Fabrikam Ops",
            "Team",
            [new(new DateOnly(2026, 9, 1), 450m, "Team subscription, 30 users, September")],
            ["INC-2291: Elevated HTTP 500 rate on api.contoso.example since 05:52 UTC. Cause identified, fix rolling out, next update 10:30 UTC."],
            Plans),
        ["lee@northwind.example"] = new(
            "Lee Chen",
            "Starter",
            [new(new DateOnly(2026, 9, 1), 96m, "Starter subscription, 12 users, September")],
            [],
            Plans),
    };

    public AccountDirectory() =>
        GetAccountTool = AIFunctionFactory.Create(GetAccount, name: "get_account");

    public AIFunction GetAccountTool { get; }

    /// <summary>The same lookup the tool performs, for demos that need the record without a tool call.</summary>
    public static object Lookup(string email) => GetAccount(email);

    [Description("Looks up a Contoso Cloud customer account by email: plan, recent charges, open incidents and the plans on offer.")]
    private static object GetAccount([Description("The customer's email address.")] string email) =>
        Accounts.TryGetValue(email.Trim(), out var account)
            ? account
            : $"No account is registered to {email}.";
}
