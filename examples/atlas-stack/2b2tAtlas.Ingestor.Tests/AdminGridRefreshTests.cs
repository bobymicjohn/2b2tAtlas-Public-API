using System.Reflection;
using Atlas.Ingestion;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;
using AdminPage = _2b2tAtlas.Client.Pages.Admin;

namespace Atlas.Ingestor.Tests;

public sealed class AdminGridRefreshTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Job_refresh_preserves_grid_page_sort_filter_and_updates_rows()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IJSRuntime, StaticJs>();
        services.AddSingleton<NavigationManager, TestNavigation>();
        services.AddRadzenComponents();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var admin = new AdminPage();
            var jobs = Enumerable.Range(0, 50).Select(i => new IngestionJobDto
            {
                Id = i.ToString(), Name = $"Build {i:00}", Status = "running", ProgressPercent = 1,
                RequestedUtc = DateTime.UtcNow.AddMinutes(-i)
            }).ToList();
            typeof(AdminPage).GetField("ingestionJobs", PrivateInstance)!.SetValue(admin, jobs);
            var refresh = typeof(AdminPage).GetMethod("RefreshDisplayedIngestionJobs", PrivateInstance)!;
            async Task Refresh() => await (Task)refresh.Invoke(admin, null)!;
            await Refresh();
            var data = (IEnumerable<IngestionJobDto>)typeof(AdminPage).GetField("DisplayedIngestionJobs", PrivateInstance)!.GetValue(admin)!;
            GridHost? host = null;
            await renderer.RenderComponentAsync<GridHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(GridHost.Rows)] = data,
                [nameof(GridHost.Ready)] = (Action<GridHost>)(value => host = value)
            }));
            var grid = host!.Grid!;
            typeof(AdminPage).GetField("ingestionJobsGrid", PrivateInstance)!.SetValue(admin, grid);
            grid.OrderByDescending(nameof(IngestionJobDto.Name));
            var column = grid.ColumnsCollection.Single();
            column.SetFilterValue("Build");
            await grid.ApplyFilter(column);
            await grid.GoToPage(2);
            Assert.Equal(2, grid.CurrentPage);
            var names = grid.PagedView.Select(j => j.Name).ToArray();

            // Exercise the page's real refresh path repeatedly, followed by the
            // unrelated parent renders produced by collector/BlueMap polling.
            for (var tick = 0; tick < 4; tick++)
            {
                jobs = jobs.Select(j => new IngestionJobDto
                {
                    Id = j.Id, Name = j.Name, Status = j.Status,
                    ProgressPercent = tick + 20, RequestedUtc = j.RequestedUtc
                }).ToList();
                typeof(AdminPage).GetField("ingestionJobs", PrivateInstance)!.SetValue(admin, jobs);
                await Refresh();
                host.RenderAgain();
                Assert.Same(data, grid.Data);
                Assert.Equal(2, grid.CurrentPage);
                Assert.Equal("Build", column.GetFilterValue());
                Assert.Equal(SortOrder.Descending, column.GetSortOrder());
                Assert.Equal(names, grid.PagedView.Select(j => j.Name));
                Assert.All(grid.PagedView, j => Assert.Equal(tick + 20, j.ProgressPercent));
            }

            jobs.RemoveRange(12, jobs.Count - 12);
            await Refresh();
            Assert.Equal(1, grid.CurrentPage);
            Assert.Equal("Build", column.GetFilterValue());
            Assert.Equal(SortOrder.Descending, column.GetSortOrder());
            Assert.NotEmpty(grid.PagedView);
        });
    }

    public sealed class GridHost : ComponentBase
    {
        [Parameter] public IEnumerable<IngestionJobDto> Rows { get; set; } = [];
        [Parameter] public Action<GridHost>? Ready { get; set; }
        public RadzenDataGrid<IngestionJobDto>? Grid { get; private set; }
        public void RenderAgain() => StateHasChanged();
        protected override void OnInitialized() => Ready?.Invoke(this);
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<RadzenDataGrid<IngestionJobDto>>(0);
            builder.AddAttribute(1, "Data", Rows);
            builder.AddAttribute(2, "AllowPaging", true);
            builder.AddAttribute(3, "PageSize", 10);
            builder.AddAttribute(4, "AllowSorting", true);
            builder.AddAttribute(5, "AllowFiltering", true);
            builder.AddAttribute(6, "Columns", (RenderFragment)(columns =>
            {
                columns.OpenComponent<RadzenDataGridColumn<IngestionJobDto>>(0);
                columns.AddAttribute(1, "Property", nameof(IngestionJobDto.Name));
                columns.CloseComponent();
            }));
            builder.AddComponentReferenceCapture(7, value => Grid = (RadzenDataGrid<IngestionJobDto>)value);
            builder.CloseComponent();
        }
    }

    private sealed class StaticJs : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult(default(TValue)!);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/admin");
    }
}
