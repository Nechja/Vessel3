using Microsoft.Extensions.Logging;
using Vessel3.Server.Configuration;
using Xunit;

namespace Vessel3.Tests;

public sealed class LoggingAndTracingTests
{
    [Fact]
    public void VesselConfig_LoggingDefaults_AreSensible()
    {
        var prevFormat = Environment.GetEnvironmentVariable("VESSEL3_LOG_FORMAT");
        var prevLevel = Environment.GetEnvironmentVariable("VESSEL3_LOG_LEVEL");
        var prevAccess = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_LOG");
        var prevNode = Environment.GetEnvironmentVariable("VESSEL3_NODE_ID");
        try
        {
            Environment.SetEnvironmentVariable("VESSEL3_LOG_FORMAT", null);
            Environment.SetEnvironmentVariable("VESSEL3_LOG_LEVEL", null);
            Environment.SetEnvironmentVariable("VESSEL3_ACCESS_LOG", null);
            Environment.SetEnvironmentVariable("VESSEL3_NODE_ID", null);

            var ok = VesselConfig.TryCreate(out var config, out var error);
            Assert.True(ok, error);
            Assert.NotNull(config);
            Assert.Equal("text", config.LogFormat);
            Assert.Equal(LogLevel.Information, config.LogLevel);
            Assert.True(config.AccessLogEnabled);
            Assert.False(string.IsNullOrEmpty(config.NodeId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESSEL3_LOG_FORMAT", prevFormat);
            Environment.SetEnvironmentVariable("VESSEL3_LOG_LEVEL", prevLevel);
            Environment.SetEnvironmentVariable("VESSEL3_ACCESS_LOG", prevAccess);
            Environment.SetEnvironmentVariable("VESSEL3_NODE_ID", prevNode);
        }
    }

    [Fact]
    public void VesselConfig_CustomLoggingEnvVars_AreParsed()
    {
        var prevFormat = Environment.GetEnvironmentVariable("VESSEL3_LOG_FORMAT");
        var prevLevel = Environment.GetEnvironmentVariable("VESSEL3_LOG_LEVEL");
        var prevAccess = Environment.GetEnvironmentVariable("VESSEL3_ACCESS_LOG");
        var prevNode = Environment.GetEnvironmentVariable("VESSEL3_NODE_ID");
        try
        {
            Environment.SetEnvironmentVariable("VESSEL3_LOG_FORMAT", "json");
            Environment.SetEnvironmentVariable("VESSEL3_LOG_LEVEL", "debug");
            Environment.SetEnvironmentVariable("VESSEL3_ACCESS_LOG", "false");
            Environment.SetEnvironmentVariable("VESSEL3_NODE_ID", "k8s-pod-vessel-0");

            var ok = VesselConfig.TryCreate(out var config, out var error);
            Assert.True(ok, error);
            Assert.NotNull(config);
            Assert.Equal("json", config.LogFormat);
            Assert.Equal(LogLevel.Debug, config.LogLevel);
            Assert.False(config.AccessLogEnabled);
            Assert.Equal("k8s-pod-vessel-0", config.NodeId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VESSEL3_LOG_FORMAT", prevFormat);
            Environment.SetEnvironmentVariable("VESSEL3_LOG_LEVEL", prevLevel);
            Environment.SetEnvironmentVariable("VESSEL3_ACCESS_LOG", prevAccess);
            Environment.SetEnvironmentVariable("VESSEL3_NODE_ID", prevNode);
        }
    }

    [Fact]
    public void RequestTrace_SetContext_UpdatesCurrentTrace()
    {
        var trace = new RequestTrace();
        RequestTrace.Current = trace;
        try
        {
            RequestTrace.SetContext(protocol: "azure", actor: "test-user", traceId: "req-abc-999");
            RequestTrace.SetTarget("PutBlob", "my-container", "my-blob.txt");

            Assert.Equal("azure", trace.Protocol);
            Assert.Equal("test-user", trace.Actor);
            Assert.Equal("req-abc-999", trace.TraceId);
            Assert.Equal("PutBlob", trace.Action);
            Assert.Equal("my-container", trace.Bucket);
            Assert.Equal("my-blob.txt", trace.Key);
        }
        finally
        {
            RequestTrace.Current = null;
        }
    }
}
