using Azure.Storage;
using Azure.Storage.Blobs;

var endpoint = Environment.GetEnvironmentVariable("VESSEL3_AZURE_ENDPOINT") ?? "http://127.0.0.1:9000/devstoreaccount1";
var accountName = Environment.GetEnvironmentVariable("VESSEL3_AZURE_ACCOUNT") ?? "devstoreaccount1";
var accountKey = Environment.GetEnvironmentVariable("VESSEL3_AZURE_KEY") ?? "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

var credential = new StorageSharedKeyCredential(accountName, accountKey);
var serviceUri = new Uri(endpoint);
var client = new BlobServiceClient(serviceUri, credential);

Console.WriteLine($"Starting Vessel3 Azure Compatibility Probe against {serviceUri} (account: {accountName})...");
return await Smoke.Run(client);
