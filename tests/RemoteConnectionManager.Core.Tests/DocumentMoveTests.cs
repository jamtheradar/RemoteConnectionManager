using RemoteConnectionManager.Core.Models;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class DocumentMoveTests
{
    private static (ConnectionsDocument Doc, ConnectionFolder Prod, ConnectionFolder Web, ConnectionFolder Test, RdpConnection Server) Sample()
    {
        var server = new RdpConnection { Name = "web01", Host = "web01" };
        var web = new ConnectionFolder { Name = "Web" };
        var prod = new ConnectionFolder { Name = "Prod", Folders = { web }, Connections = { server } };
        var test = new ConnectionFolder { Name = "Test" };
        var doc = new ConnectionsDocument { Folders = { prod, test } };
        return (doc, prod, web, test, server);
    }

    [Fact]
    public void MoveEntry_ToAnotherFolder()
    {
        var (doc, prod, _, test, server) = Sample();
        Assert.True(doc.Move(server, test));
        Assert.DoesNotContain(server, prod.Connections);
        Assert.Same(test, doc.ParentOf(server));
    }

    [Fact]
    public void MoveEntry_ToTopLevel()
    {
        var (doc, _, _, _, server) = Sample();
        Assert.True(doc.Move(server, null));
        Assert.Contains(server, doc.Connections);
        Assert.Null(doc.ParentOf(server));
        Assert.Equal(1, doc.EntryCount);
    }

    [Fact]
    public void MoveEntry_ToSameFolder_IsRefused()
    {
        var (doc, prod, _, _, server) = Sample();
        Assert.False(doc.CanMove(server, prod));
        Assert.False(doc.Move(server, prod));
        Assert.Single(prod.Connections);
    }

    [Fact]
    public void MoveEntry_ToFolderNotInDocument_IsRefused()
    {
        var (doc, prod, _, _, server) = Sample();
        Assert.False(doc.Move(server, new ConnectionFolder { Name = "Elsewhere" }));
        Assert.Same(prod, doc.ParentOf(server));
    }

    [Fact]
    public void MoveFolder_TakesEverythingInside()
    {
        var (doc, prod, web, test, server) = Sample();
        web.Connections.Add(new RdpConnection { Name = "web02" });
        Assert.True(doc.Move(prod, test));
        Assert.DoesNotContain(prod, doc.Folders);
        Assert.Same(test, doc.ParentOf(prod));
        Assert.Equal([test, prod], doc.ChainFor(server));
        Assert.Equal(2, doc.EntryCount);
    }

    [Fact]
    public void MoveFolder_IntoItselfOrBelowItself_IsRefused()
    {
        var (doc, prod, web, _, _) = Sample();
        Assert.False(doc.CanMove(prod, prod));
        Assert.False(doc.CanMove(prod, web));
        Assert.False(doc.Move(prod, web));
        Assert.Contains(prod, doc.Folders);
    }

    [Fact]
    public void MoveFolder_OutToTopLevel()
    {
        var (doc, prod, web, _, _) = Sample();
        Assert.False(doc.CanMove(prod, null));
        Assert.True(doc.Move(web, null));
        Assert.Contains(web, doc.Folders);
        Assert.Empty(prod.Folders);
    }
}
