namespace SupportDeskSample.Demos;

/// <summary>A ticket with the routing a human would have chosen, used to score both models.</summary>
public sealed record LabeledTicket(string Text, Department Department, bool Urgent);

public static class DemoData
{
    /// <summary>
    /// Ten tickets. The last three are deliberately ambiguous: they mention billing words but are really
    /// technical or sales, which is where a confidence threshold earns its keep.
    /// </summary>
    public static IReadOnlyList<LabeledTicket> Labeled { get; } =
    [
        new("I was charged $49 twice this month. Please refund the duplicate. - dana@example.com", Department.Billing, true),
        new("Every API call has returned HTTP 500 since 6am and checkout is down. - ops@fabrikam.example", Department.Technical, true),
        new("Does the Team plan include SSO, and what would 12 seats cost? - lee@northwind.example", Department.Sales, false),
        new("Please send a copy of the September invoice for our records. No rush.", Department.Billing, false),
        new("The webhook signature check fails with a 401 after your certificate rotation.", Department.Technical, true),
        new("We'd like to add 30 seats before the end of the quarter. Who do we talk to?", Department.Sales, false),
        new("Your dashboard shows my card as expired but the bank says it is fine.", Department.Billing, false),
        new("The invoice total is wrong because the usage export double counts API calls after the 3.2 upgrade.", Department.Technical, true),
        new("Can you explain the overage pricing tiers before we sign the renewal?", Department.Sales, false),
        new("My payment failed and now my production key is disabled. Orders are not going through.", Department.Billing, true),
    ];

    /// <summary>A weak reply, used to show the reviewer rejecting something it should reject.</summary>
    public const string WeakDraft =
        "That is not something we handle. Read the billing FAQ on our website. Closing this ticket.";
}
