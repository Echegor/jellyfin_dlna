using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Dlna.ContentDirectory;
using Jellyfin.Plugin.Dlna.Model;
using Jellyfin.Plugin.Dlna.Didl;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Library;
using MediaBrowser.Controller.Dto;
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
    public void SelectingEachUserOpensTheirNormalLandingAndCarriesIdentityToChildren()
    {
        var users = new[] { NewUser("Alice"), NewUser("Bob") };
        var manager = new Mock<IUserManager>();
        manager.Setup(x => x.GetUsers()).Returns(users);
        var views = new Mock<IUserViewManager>();
        views.Setup(x => x.GetUserViews(It.IsAny<UserViewQuery>())).Returns(Array.Empty<UserView>());
        foreach (var user in users)
        {
            manager.Setup(x => x.GetUserById(user.Id)).Returns(user);
            var handler = Handler(manager.Object, views: views.Object);
            var root = Browse(handler, "0", 0, 10);
            var didl = XElement.Parse(root.Element("Result")!.Value);
            var id = didl.Elements().Single(x => x.Elements().Any(e => e.Name.LocalName == "title" && e.Value == user.Username)).Attribute("id")!.Value;
            Browse(handler, id, 0, 10);
            views.Verify(x => x.GetUserViews(It.Is<UserViewQuery>(q => q.User == user)), Times.Once);
            var builder = (DidlBuilder)typeof(ControlHandler).GetField("_didlBuilder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(handler)!;
            Assert.Same(user, builder.User);
            Assert.StartsWith($"u_{user.Id:N}_", builder.GetClientId(Guid.NewGuid(), null), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DefaultUserSkipsPickerAndOverridesCachedUserFolders()
    {
        var selected = NewUser("Selected");
        var other = NewUser("Other");
        var manager = new Mock<IUserManager>();
        manager.Setup(x => x.GetUserById(selected.Id)).Returns(selected);
        manager.Setup(x => x.GetUserById(other.Id)).Returns(other);
        var views = new Mock<IUserViewManager>();
        views.Setup(x => x.GetUserViews(It.IsAny<UserViewQuery>())).Returns(Array.Empty<UserView>());
        var handler = Handler(manager.Object, selected, views.Object);
        Browse(handler, "0", 0, 10);
        Browse(handler, $"u_{other.Id:N}_0", 0, 10);
        manager.Verify(x => x.GetUsers(), Times.Never);
        views.Verify(x => x.GetUserViews(It.Is<UserViewQuery>(q => q.User == selected)), Times.Exactly(2));
        views.Verify(x => x.GetUserViews(It.Is<UserViewQuery>(q => q.User == other)), Times.Never);
    }

    [Fact]
    public void DefaultUserResolutionSupportsNoneAndRejectsDeletedUsers()
    {
        var selected = NewUser("Selected");
        var manager = new Mock<IUserManager>();
        manager.Setup(x => x.GetUserById(selected.Id)).Returns(selected);
        Assert.Null(ContentDirectoryService.ResolveDefaultUser(null, manager.Object));
        Assert.Null(ContentDirectoryService.ResolveDefaultUser(Guid.Empty, manager.Object));
        Assert.Same(selected, ContentDirectoryService.ResolveDefaultUser(selected.Id, manager.Object));
        Assert.Throws<InvalidOperationException>(() => ContentDirectoryService.ResolveDefaultUser(Guid.NewGuid(), manager.Object));
    }

    private static User NewUser(string name) => new(name, "password", "reset") { Id = Guid.NewGuid() };

    private static ControlHandler Handler(IUserManager manager, User? user = null, IUserViewManager? views = null)
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(x => x.GetUserRootFolder()).Returns(new UserRootFolder { Id = Guid.NewGuid() });
        return new ControlHandler(NullLogger.Instance, library.Object, new DlnaDeviceProfile(), "http://localhost", null,
            Mock.Of<IImageProcessor>(), Mock.Of<IUserDataManager>(), user, 1, null!, Mock.Of<IMediaSourceManager>(),
            views ?? Mock.Of<IUserViewManager>(), Mock.Of<IMediaEncoder>(), Mock.Of<ITVSeriesManager>(), manager);
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
