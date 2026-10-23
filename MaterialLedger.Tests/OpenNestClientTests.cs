using System.Net;
using System.Text;
using MaterialLedger.Web.Services;
using Xunit;

namespace MaterialLedger.Tests;

public sealed class OpenNestClientTests
{
    private const string EmptyPage = """{"items":[],"total":0,"offset":0,"limit":100}""";

    [Fact]
    public async Task QueryNestsAsync_sends_status_search_sort_and_paging()
    {
        var handler = new StubHandler(HttpStatusCode.OK, EmptyPage);
        var client = CreateClient(handler);

        await client.QueryNestsAsync(new OpenNestQuery("toBeCut", "acme & sons", Offset: 20, Limit: 50), CancellationToken.None);

        Assert.Equal(
            "/api/nests/query?status=toBeCut&search=acme%20%26%20sons&sort=dateCreated&order=desc&offset=20&limit=50",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task QueryNestsAsync_omits_null_status_and_blank_search(string? search)
    {
        var handler = new StubHandler(HttpStatusCode.OK, EmptyPage);
        var client = CreateClient(handler);

        await client.QueryNestsAsync(new OpenNestQuery(null, search), CancellationToken.None);

        Assert.Equal(
            "/api/nests/query?sort=dateCreated&order=desc&offset=0&limit=100",
            handler.LastRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task QueryNestsAsync_reads_page_and_camel_case_status()
    {
        var handler = new StubHandler(HttpStatusCode.OK, $$"""
            {"items":[{{NestJson("toBeCut")}}],"total":7,"offset":5,"limit":1}
            """);
        var client = CreateClient(handler);

        var page = await client.QueryNestsAsync(new OpenNestQuery("toBeCut", null), CancellationToken.None);

        Assert.Equal(7, page.Total);
        Assert.Equal(5, page.Offset);
        Assert.Equal(1, page.Limit);
        var nest = Assert.Single(page.Items);
        Assert.Equal("toBeCut", nest.Status);
        Assert.Equal("N-1001", nest.Name);
        Assert.Equal(Guid.Parse("3f2b6c1e-8d4a-4f6b-9a2e-1c5d7e9f0a1b"), nest.Id);
    }

    [Fact]
    public async Task GetNestAsync_maps_record_fields_and_rounds_thickness_to_four_places()
    {
        var handler = new StubHandler(HttpStatusCode.OK, NestJson("hasBeenCut", thickness: "0.18750000001"));
        var client = CreateClient(handler);
        var id = Guid.Parse("3f2b6c1e-8d4a-4f6b-9a2e-1c5d7e9f0a1b");

        var nest = await client.GetNestAsync(id, CancellationToken.None);

        Assert.NotNull(nest);
        Assert.Equal($"/api/nests/{id}", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal(3, nest.Revision);
        Assert.False(nest.Deleted);
        Assert.Equal("Acme", nest.Customer);
        Assert.Equal("Steel", nest.Material);
        Assert.Equal("A36", nest.MaterialGrade);
        Assert.Equal(0.1875m, nest.Thickness);
        Assert.Equal("hasBeenCut", nest.Status);
        Assert.Equal(2, nest.PlateCount);
        Assert.Equal(14, nest.PartCount);
        Assert.Equal("rush", nest.Comments);
        Assert.Equal("aj", nest.MadeBy);
        Assert.Equal(12345, nest.FileSize);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc), nest.DateCreated);
    }

    [Fact]
    public async Task GetNestAsync_keeps_unknown_status_as_text()
    {
        var handler = new StubHandler(HttpStatusCode.OK, NestJson("onHold"));
        var client = CreateClient(handler);

        var nest = await client.GetNestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal("onHold", nest!.Status);
    }

    [Fact]
    public async Task GetNestAsync_returns_null_on_404()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.NotFound, ""));

        Assert.Null(await client.GetNestAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetSheetsAsync_returns_null_on_404()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.NotFound, ""));

        Assert.Null(await client.GetSheetsAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetNestAsync_throws_on_server_error()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.InternalServerError, ""));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetNestAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetSheetsAsync_rounds_sizes_to_three_places()
    {
        var id = Guid.Parse("3f2b6c1e-8d4a-4f6b-9a2e-1c5d7e9f0a1b");
        var handler = new StubHandler(HttpStatusCode.OK, $$"""
            {
              "nestId":"{{id}}",
              "revision":4,
              "sheets":[
                {"width":48.00000001,"length":95.99999999,"quantity":3},
                {"width":60.0625,"length":120.1875,"quantity":1}
              ],
              "parts":[{"name":"BRACKET","required":10,"nested":8}]
            }
            """);
        var client = CreateClient(handler);

        var sheets = await client.GetSheetsAsync(id, CancellationToken.None);

        Assert.NotNull(sheets);
        Assert.Equal($"/api/nests/{id}/sheets", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal(id, sheets.NestId);
        Assert.Equal(4, sheets.Revision);
        Assert.Collection(sheets.Sheets,
            s => { Assert.Equal(48.000m, s.Width); Assert.Equal(96.000m, s.Length); Assert.Equal(3, s.Quantity); },
            s => { Assert.Equal(60.063m, s.Width); Assert.Equal(120.188m, s.Length); Assert.Equal(1, s.Quantity); });
        var part = Assert.Single(sheets.Parts);
        Assert.Equal("BRACKET", part.Name);
        Assert.Equal(10, part.Required);
        Assert.Equal(8, part.Nested);
    }

    [Fact]
    public async Task GetNestAsync_keeps_eighth_fraction_thickness_exact()
    {
        var client = CreateClient(new StubHandler(HttpStatusCode.OK, NestJson("toBeCut", thickness: "0.1875")));

        var nest = await client.GetNestAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(0.1875m, nest!.Thickness);
    }

    private static string NestJson(string status, string thickness = "0.25") => $$"""
        {
          "id":"3f2b6c1e-8d4a-4f6b-9a2e-1c5d7e9f0a1b",
          "revision":3,
          "deleted":false,
          "restoredFromRevision":null,
          "name":"N-1001",
          "customer":"Acme",
          "dateCreated":"2026-10-01T08:30:00Z",
          "dateModified":"2026-10-02T09:00:00Z",
          "material":"Steel",
          "materialGrade":"A36",
          "thickness":{{thickness}},
          "status":"{{status}}",
          "plateCount":2,
          "partCount":14,
          "comments":"rush",
          "madeBy":"aj",
          "fileSize":12345,
          "savedAt":"2026-10-02T09:00:00Z"
        }
        """;

    private static OpenNestClient CreateClient(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://opennest.test/") });

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
