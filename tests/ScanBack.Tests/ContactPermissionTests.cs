using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class ContactPermissionTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Owner_cannot_restore_recipient_notifications_by_reenabling_the_contact()
    {
        var rider = await fixture.SeedRider();
        await fixture.InDatabase(async db =>
        {
            var contact = (await db.EmergencyContacts.FindAsync(rider.Contact.Id))!;
            contact.NotifyEnabled = false;
            contact.NotificationsRequested = false;
            contact.NotificationsConsentedAt = null;
            await db.SaveChangesAsync();
        });
        using var owner = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(owner, "/contacts/edit/" + rider.Contact.Id);
        var edited = await owner.PostAsync("/contacts/edit/" + rider.Contact.Id, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Name"] = rider.Contact.Name, ["Relationship"] = rider.Contact.Relationship,
            ["Email"] = rider.Contact.Email, ["Phone"] = rider.Contact.Phone, ["NotifyEnabled"] = "true", ["PublicPhone"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, edited.StatusCode);
        var pending = await fixture.InDatabase(db => db.EmergencyContacts.AsNoTracking().SingleAsync(x => x.Id == rider.Contact.Id));
        Assert.False(pending.NotifyEnabled);
        Assert.Null(pending.NotificationsConsentedAt);
        Assert.NotEmpty(pending.VerificationTokenHash);
        Assert.Equal(1, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.EmergencyContactId == rider.Contact.Id && x.Kind == "ContactVerification")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recipient_grants_notifications_and_public_phone_independently_then_can_withdraw_both(bool notifications, bool publicPhone)
    {
        var rider = await fixture.SeedRider(PublicFields.Contacts);
        using var owner = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        var email = "recipient-consent-" + Guid.NewGuid().ToString("N") + "@example.test";
        var name = "New contact " + Guid.NewGuid().ToString("N");
        var csrf = await SqlWebFixture.Antiforgery(owner, "/contacts/create");
        var created = await owner.PostAsync("/contacts/create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Name"] = name, ["Relationship"] = "Family",
            ["Email"] = email, ["Phone"] = "+306900000099", ["NotifyEnabled"] = "true", ["PublicPhone"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var contact = await fixture.InDatabase(db => db.EmergencyContacts.AsNoTracking().SingleAsync(x => x.Email == email));
        Assert.True(contact.PublicPhoneRequested);
        Assert.False(contact.PublicPhone);
        Assert.Null(contact.PublicPhoneConsentedAt);
        using var recipient = fixture.Client();
        Assert.DoesNotContain(name, await recipient.GetStringAsync("/e/" + rider.Token));
        var verification = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EmergencyContactId == contact.Id && x.Kind == "ContactVerification"));
        var verifyLink = Regex.Match(verification.Body, @"http://localhost/contacts/verify/[^\s]+").Value;
        Assert.NotEmpty(verifyLink);
        csrf = await SqlWebFixture.Antiforgery(recipient, verifyLink);
        Assert.False(await fixture.InDatabase(db => db.EmergencyContacts.Where(x => x.Id == contact.Id).Select(x => x.IsVerified).SingleAsync()));
        var confirmed = await recipient.PostAsync(verifyLink, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["PolicyVersion"] = SqlWebFixture.PolicyVersion,
            ["AcceptNotifications"] = notifications.ToString(), ["AcceptPublicPhone"] = publicPhone.ToString()
        }));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var granted = await fixture.InDatabase(db => db.EmergencyContacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id));
        Assert.True(granted.IsVerified);
        Assert.Equal(notifications, granted.NotifyEnabled);
        Assert.Equal(notifications, granted.NotificationsConsentedAt is not null);
        Assert.Equal(publicPhone, granted.PublicPhone);
        Assert.Equal(publicPhone, granted.PublicPhoneConsentedAt is not null);
        Assert.Equal(SqlWebFixture.PolicyVersion, granted.ConsentPolicyVersion);
        Assert.Empty(granted.VerificationTokenHash);
        var publicPage = await recipient.GetStringAsync("/e/" + rider.Token);
        if (publicPhone) Assert.Contains(name, publicPage);
        else Assert.DoesNotContain(name, publicPage);

        // Reusing the consumed confirmation cannot broaden the recipient's choices.
        await recipient.PostAsync(verifyLink, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["PolicyVersion"] = SqlWebFixture.PolicyVersion,
            ["AcceptNotifications"] = "true", ["AcceptPublicPhone"] = "true"
        }));
        var replayed = await fixture.InDatabase(db => db.EmergencyContacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id));
        Assert.Equal(notifications, replayed.NotifyEnabled);
        Assert.Equal(publicPhone, replayed.PublicPhone);

        var unsubscribe = Regex.Match(verification.Body, @"http://localhost/contacts/unsubscribe/[^\s]+").Value;
        Assert.NotEmpty(unsubscribe);
        csrf = await SqlWebFixture.Antiforgery(recipient, unsubscribe);
        Assert.Equal(HttpStatusCode.OK, (await recipient.PostAsync(unsubscribe, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }))).StatusCode);
        var withdrawn = await fixture.InDatabase(db => db.EmergencyContacts.AsNoTracking().SingleAsync(x => x.Id == contact.Id));
        Assert.False(withdrawn.NotifyEnabled);
        Assert.False(withdrawn.PublicPhone);
        Assert.Null(withdrawn.NotificationsConsentedAt);
        Assert.Null(withdrawn.PublicPhoneConsentedAt);
        Assert.DoesNotContain(name, await recipient.GetStringAsync("/e/" + rider.Token));
    }
}
