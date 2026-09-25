namespace SupportDeskSample;

/// <summary>Tickets used when no <c>--ticket</c> argument is given. Each email has an account in <see cref="AccountDirectory"/>.</summary>
public static class SampleTickets
{
    public static IReadOnlyList<string> All { get; } =
    [
        """
        I was charged twice for my Pro subscription this month, $49 on Sep 1 and again on Sep 2.
        Please refund the duplicate today, my card is close to its limit. - dana@example.com
        """,
        """
        Since 6am every call to your API returns HTTP 500 and our checkout is completely down.
        We are losing orders every minute. We need an update NOW. - ops@fabrikam.example
        """,
        """
        Hi! We're a team of 12 on the Starter plan and our security team now requires SSO.
        Is there a plan that includes it, and what would it cost us? No rush. - lee@northwind.example
        """,
    ];
}
