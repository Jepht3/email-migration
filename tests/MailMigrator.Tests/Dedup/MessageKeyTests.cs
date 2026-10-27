using MailMigrator.Core.Dedup;
using MailMigrator.Core.Imap;

namespace MailMigrator.Tests.Dedup;

public class MessageKeyTests
{
    private static readonly DateTimeOffset Date = new(2024, 3, 12, 9, 30, 0, TimeSpan.FromHours(1));

    [Theory]
    [InlineData("<ABC.123@Mail.Example.FR>")]
    [InlineData("abc.123@mail.example.fr")]
    [InlineData("  <abc.123@mail.example.fr>  ")]
    public void Message_id_is_normalized(string messageId)
    {
        Assert.Equal("mid:abc.123@mail.example.fr", MessageKey.Compute(messageId, Date, "a@b.fr", "Sujet", 100));
    }

    [Fact]
    public void Message_id_wins_over_other_fields()
    {
        var a = MessageKey.Compute("<x@y>", Date, "a@b.fr", "Devis", 100);
        var b = MessageKey.Compute("<x@y>", Date.AddDays(3), "c@d.fr", "Autre", 999);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Fallback_hash_is_stable_and_insensitive_to_case_spacing_and_time_zone()
    {
        var a = MessageKey.Compute(null, Date, "Jean@Exemple.fr", "Devis  client Dupont", 2048);
        var b = MessageKey.Compute("  ", Date.ToUniversalTime(), " jean@exemple.fr", "devis client dupont", 2048);

        Assert.StartsWith("h:", a);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("autre@exemple.fr", "Devis", 2048, 0)]
    [InlineData("jean@exemple.fr", "Re: Devis", 2048, 0)]
    [InlineData("jean@exemple.fr", "Devis", 2049, 0)]
    [InlineData("jean@exemple.fr", "Devis", 2048, 1)]
    public void Fallback_hash_differs_when_a_field_differs(string from, string subject, long size, int minutesLater)
    {
        var reference = MessageKey.Compute(null, Date, "jean@exemple.fr", "Devis", 2048);

        Assert.NotEqual(reference, MessageKey.Compute(null, Date.AddMinutes(minutesLater), from, subject, size));
    }

    [Fact]
    public void Remote_message_uses_header_date_then_internal_date()
    {
        var withDate = new RemoteMessage(1, null, Date.AddHours(5), Date, "Jean", "jean@exemple.fr", "Devis", 10, MailFlags.None);
        var withoutDate = withDate with { Date = null, InternalDate = Date };

        Assert.Equal(MessageKey.For(withDate), MessageKey.For(withoutDate));
    }
}
