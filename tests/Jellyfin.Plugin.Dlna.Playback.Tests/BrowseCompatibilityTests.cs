using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Dlna.ContentDirectory;
using Jellyfin.Plugin.Dlna.Model;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Dlna.Playback.Tests;

public class BrowseCompatibilityTests
{
    [Theory]
    [InlineData(0, 1, "Alice", 1)]
    [InlineData(1, 1, "Bob", 1)]
    [InlineData(2, 5, "Charlie", 1)]
    [InlineData(3, 1, null, 0)]
    public void RootPickerPaginatesWithoutClaimingUsersAreEmpty(int start, int count, string? title, int returned)
    {
        var users = new[] { NewUser("Charlie"), NewUser("Alice"), NewUser("Bob") };
        var manager = new Mock<IUserManager>();
        manager.Setup(x => x.GetUsers()).Returns(users);
        var handler = Handler(manager.Object);
        var response = Browse(handler, "0", start, count);
        Assert.Equal("3", response.Element("TotalMatches")!.Value);
        Assert.Equal(returned.ToString(CultureInfo.InvariantCulture), response.Element("NumberReturned")!.Value);
        var didl = XElement.Parse(response.Element("Result")!.Value);
        var containers = didl.Elements().ToArray();
        Assert.Equal(returned, containers.Length);
        if (title is not null)
        {
            var container = Assert.Single(containers);
            Assert.Null(container.Attribute("childCount"));
            Assert.Equal(title, container.Elements().Single(x => x.Name.LocalName == "title").Value);
        }
    }

    [Fact]
    public void ConfiguredUserPrecedenceAndPickerFallbackArePreserved()
    {
        var profile = NewUser("Profile");
        var fallback = NewUser("Default");
        var manager = new Mock<IUserManager>();
        manager.Setup(x => x.GetUserById(profile.Id)).Returns(profile);
        manager.Setup(x => x.GetUserById(fallback.Id)).Returns(fallback);
        Assert.Same(profile, ContentDirectoryService.ResolveUser(profile.Id.ToString(), fallback.Id, manager.Object));
        Assert.Same(fallback, ContentDirectoryService.ResolveUser(null, fallback.Id, manager.Object));
        Assert.Null(ContentDirectoryService.ResolveUser(null, null, manager.Object));
        Assert.Throws<InvalidOperationException>(() => ContentDirectoryService.ResolveUser("invalid", fallback.Id, manager.Object));
        Assert.Throws<InvalidOperationException>(() => ContentDirectoryService.ResolveUser(Guid.NewGuid().ToString(), fallback.Id, manager.Object));
    }

    [Fact]
    public void ConfiguredDeviceCannotSwitchUsersThroughObjectId()
    {
        var configured = NewUser("Configured");
        var manager = new Mock<IUserManager>();
        var handler = Handler(manager.Object, configured);
        var error = Assert.Throws<TargetInvocationException>(() => Browse(handler, $"u_{Guid.NewGuid():N}_0", 0, 1));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    private static User NewUser(string name) => new(name, "password", "reset") { Id = Guid.NewGuid() };

    private static ControlHandler Handler(IUserManager manager, User? user = null)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetUserRootFolder()).Returns(new UserRootFolder { Id = Guid.NewGuid() });
        return new ControlHandler(NullLogger.Instance, library.Object, new DlnaDeviceProfile(), "http://localhost", null,
            Mock.Of<IImageProcessor>(), Mock.Of<IUserDataManager>(), user, 1, null!, Mock.Of<IMediaSourceManager>(),
            Mock.Of<IUserViewManager>(), Mock.Of<IMediaEncoder>(), Mock.Of<ITVSeriesManager>(), manager);
    }

    private static XElement Browse(ControlHandler handler, string id, int start, int count)
    {
        using var output = new StringWriter();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { OmitXmlDeclaration = true }))
        {
            writer.WriteStartElement("Response");
            typeof(ControlHandler).GetMethod("HandleBrowse", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(handler,
                [writer, new Dictionary<string, string> { ["ObjectID"] = id, ["BrowseFlag"] = "BrowseDirectChildren",
                    ["StartingIndex"] = start.ToString(CultureInfo.InvariantCulture), ["RequestedCount"] = count.ToString(CultureInfo.InvariantCulture) }, "test"]);
            writer.WriteEndElement();
        }

        return XElement.Parse(output.ToString());
    }
}
