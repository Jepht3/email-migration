using MailMigrator.App.ViewModels;
using MailMigrator.Core.Connectivity;
using MailMigrator.Core.Models;

// Immediate email recognition must not need network or launch a browser.
var account = new AccountSideViewModel("Source", new AccountServices(null!, null!, null!));
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
account.Email = "person@gmail.com";
Check(account.AuthMethod == OAuthProvider.Google && account.UsesOAuth && !account.UsesPassword, "Gmail must select OAuth");
Check(account.OAuthButtonLabel == "Se connecter avec Google", "Google button label");
Check(account.Endpoint?.Host == "imap.gmail.com", "Google endpoint");
account.Email = "second@gmail.com";
Check(account.Endpoint?.UserName == "second@gmail.com", "Same provider must update the username");
account.Email = "person@outlook.fr";
Check(account.AuthMethod == OAuthProvider.Microsoft && account.Endpoint?.Host == "outlook.office365.com", "Switch Google to Microsoft");
Check(account.OAuthButtonLabel == "Se connecter avec Microsoft", "Microsoft button label");
account.AuthMethod = OAuthProvider.None;
Check(account.UsesPassword, "Explicit password fallback remains possible");
account.Email = "person@googlemail.com";
Check(account.AuthMethod == OAuthProvider.Google, "New address resets the manual choice");
account.Email = "person@example.org";
Check(account.UsesPassword && account.Secret is null, "Unknown provider must not retain OAuth");
// Cancel the delayed unknown-domain lookup before this network-free test ends.
account.Email = "person@hotmail.com";
Check(account.AuthMethod == OAuthProvider.Microsoft, "Hotmail selects Microsoft");
account.SetMailbox("saved@example.org", new ImapEndpoint("mail.example.org", 993, SecurityMode.SslOnConnect, "saved-user"));
Check(account.UsesPassword && account.Endpoint?.UserName == "saved-user", "Saved explicit configuration remains intact");
Console.WriteLine("OAuth UI smoke checks passed.");
