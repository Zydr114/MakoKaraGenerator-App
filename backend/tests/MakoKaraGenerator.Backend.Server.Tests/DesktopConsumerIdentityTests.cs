namespace MakoKaraGenerator.Backend.Server.Tests;

public sealed class DesktopConsumerIdentityTests
{
    [Fact]
    public void LaunchIdentityRejectsPartialConfigurationAndWrongCredentials()
    {
        const string id = "0123456789abcdef0123456789abcdef";
        string secret = new('a', 64);
        Assert.Null(DesktopConsumerIdentity.Create(null, null, null));
        Assert.Throws<InvalidOperationException>(() => DesktopConsumerIdentity.Create(id, null, "1"));
        Assert.Throws<InvalidOperationException>(() => DesktopConsumerIdentity.Create(id, secret, "01"));
        var identity = DesktopConsumerIdentity.Create(id, secret, "123")!;
        Assert.True(identity.Authenticate(id, secret));
        Assert.False(identity.Authenticate(id, new string('b', 64)));
        Assert.False(identity.Authenticate("other", secret));
        Assert.False(identity.Authenticate(id, null));
    }
}
