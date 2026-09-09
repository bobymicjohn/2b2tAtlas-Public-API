using System.Net;
using _2b2tAtlas.Server.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Ingestor.Tests;

public sealed class PlaceTilesControllerTests
{
    [Fact]
    public async Task Failed_child_uses_floor_correct_cached_parent()
    {
        var handler = new ParentFallbackHandler();
        var controller = new PlaceTilesController(
            new StubHttpClientFactory(handler),
            NullLogger<PlaceTilesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.Get("base", 4, 1, 0, 0, "t.-6.-2.webp", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal([1, 2, 3], file.FileContents);
        Assert.Equal("5", controller.Response.Headers["X-Atlas-Place-Lod"]);
        Assert.Equal("-3", controller.Response.Headers["X-Atlas-Place-Tx"]);
        Assert.Equal("-1", controller.Response.Headers["X-Atlas-Place-Ty"]);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        Assert.Equal("public, max-age=900", controller.Response.Headers["CDN-Cache-Control"]);
        Assert.Equal([
            "https://2b2t.place/tiles/base/4/1/0/0/t.-6.-2.webp",
            "http://127.0.0.1:5297/tiles/place/base/5/1/0/0/t.-3.-1.webp",
        ], handler.Requests);
    }

    [Fact]
    public async Task Cached_parent_propagates_grandparent_source_metadata()
    {
        var handler = new ParentFallbackHandler(useGrandparentHeaders: true);
        var controller = new PlaceTilesController(
            new StubHttpClientFactory(handler),
            NullLogger<PlaceTilesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _ = await controller.Get("base", 4, 1, 0, 0, "t.-6.-2.webp", CancellationToken.None);

        Assert.Equal("6", controller.Response.Headers["X-Atlas-Place-Lod"]);
        Assert.Equal("-2", controller.Response.Headers["X-Atlas-Place-Tx"]);
        Assert.Equal("-1", controller.Response.Headers["X-Atlas-Place-Ty"]);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ParentFallbackHandler(bool useGrandparentHeaders = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Requests.Count == 1)
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)521));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                    {
                        Headers = { ContentType = new("image/webp") },
                    },
                };
            if (useGrandparentHeaders)
            {
                response.Headers.Add("X-Atlas-Place-Lod", "6");
                response.Headers.Add("X-Atlas-Place-Tx", "-2");
                response.Headers.Add("X-Atlas-Place-Ty", "-1");
            }
            return Task.FromResult(response);
        }
    }
}