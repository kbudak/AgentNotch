using AgentNotch.App.Usage;
using Xunit;

namespace AgentNotch.Tests;

public sealed class CursorUsageTests
{
    [Fact]
    public void CleanCursorModel_NormalizesModelNames()
    {
        Assert.Equal("Sonnet 3.5", CursorUsageClient.CleanCursorModel("claude-3-5-sonnet-20241022"));
        Assert.Equal("Sonnet 3.7", CursorUsageClient.CleanCursorModel("claude-3-7-sonnet"));
        Assert.Equal("GPT-4o", CursorUsageClient.CleanCursorModel("gpt-4o"));
        Assert.Equal("o1", CursorUsageClient.CleanCursorModel("o1-preview"));
        Assert.Equal("o3", CursorUsageClient.CleanCursorModel("o3-mini"));
        Assert.Equal("Composer", CursorUsageClient.CleanCursorModel("default"));
        Assert.Equal("Composer", CursorUsageClient.CleanCursorModel(""));
    }

    [Fact]
    public void ParseModelPreference_ExtractsModel()
    {
        var json = "{\"composer\":\"claude-3-5-sonnet-20241022\"}";
        var model = CursorUsageClient.ParseModelPreference(json);
        Assert.Equal("Sonnet 3.5", model);

        var defaultJson = "{\"composer\":\"default\"}";
        Assert.Equal("Composer", CursorUsageClient.ParseModelPreference(defaultJson));
    }

    [Fact]
    public void ParseUsageJson_ExtractsPlanAndAutoUsage()
    {
        var json = """
        {
            "billingCycleEnd": "1730000000000",
            "planUsage": {
                "totalPercentUsed": 45.2,
                "autoPercentUsed": 12.0
            }
        }
        """;

        var windows = CursorUsageClient.ParseUsageJson(json);
        Assert.Equal(2, windows.Count);

        var auto = windows[0];
        Assert.Equal("Auto", auto.Label);
        Assert.Equal(12, auto.UsedPct);
        Assert.NotNull(auto.ResetsAtUtc);

        var plan = windows[1];
        Assert.Equal("Plan", plan.Label);
        Assert.Equal(45.2, plan.UsedPct);
        Assert.NotNull(plan.ResetsAtUtc);
    }
}
