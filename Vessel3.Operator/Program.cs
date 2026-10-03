using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel3.Operator.Adapters.Kubernetes;
using Vessel3.Operator.Adapters.Vessel;
using Vessel3.Operator.Domain;
using Vessel3.Operator.Hosted;
using Vessel3.Operator.Ports;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddSingleton<IKubernetes>(_ =>
{
    var config = KubernetesClientConfiguration.IsInCluster()
        ? KubernetesClientConfiguration.InClusterConfig()
        : BuildLocalConfig();

    return new Kubernetes(config);
});

builder.Services.AddSingleton<IKubernetesPort, KubernetesApiAdapter>();
builder.Services.AddSingleton<IVesselPortFactory, VesselPortFactory>();
builder.Services.AddSingleton<ServerReconciler>();
builder.Services.AddSingleton<BucketReconciler>();
builder.Services.AddSingleton<UserReconciler>();
builder.Services.AddHostedService<OperatorWorker>();

var app = builder.Build();
await app.RunAsync();

static KubernetesClientConfiguration BuildLocalConfig()
{
    var host = Environment.GetEnvironmentVariable("KUBERNETES_HOST");
    if (!string.IsNullOrEmpty(host))
    {
        return new KubernetesClientConfiguration { Host = host };
    }

    try
    {
        return KubernetesClientConfiguration.BuildConfigFromConfigFile();
    }
    catch
    {
        return new KubernetesClientConfiguration { Host = "http://127.0.0.1:8001" };
    }
}
