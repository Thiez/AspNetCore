// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR.Client;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Components.Server.BlazorPack;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Browser;

namespace Ignitor
{
    internal class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("a uri is required");
                return 1;
            }

            Console.WriteLine("Press the ANY key to begin.");
            //Console.ReadLine();

            var uri = new Uri(args[0]);

            var program = new Program();
            Console.CancelKeyPress += (sender, e) => { program.Cancel(); };

            await program.ExecuteAsync(uri);
            return 0;
        }

        public Program()
        {
            CancellationTokenSource = new CancellationTokenSource();
            TaskCompletionSource = new TaskCompletionSource<object>();

            CancellationTokenSource.Token.Register(() =>
            {
                TaskCompletionSource.TrySetCanceled();
            });
        }

        private CancellationTokenSource CancellationTokenSource { get; }
        private CancellationToken CancellationToken => CancellationTokenSource.Token;
        private TaskCompletionSource<object> TaskCompletionSource { get; }

        public async Task ExecuteAsync(Uri uri)
        {
            var httpClient = new HttpClient();
            var response = await httpClient.GetAsync(uri);
            var content = await response.Content.ReadAsStringAsync();

            // <!-- M.A.C.Component:{"circuitId":"CfDJ8KZCIaqnXmdF...PVd6VVzfnmc1","rendererId":"0","componentId":"0"} -->
            var match = Regex.Match(content, $"{Regex.Escape("<!-- M.A.C.Component:")}(.+?){Regex.Escape(" -->")}");
            var json = JsonDocument.Parse(match.Groups[1].Value);
            var circuitId = json.RootElement.GetProperty("circuitId").GetString();

            var builder = new HubConnectionBuilder();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHubProtocol, BlazorPackHubProtocol>());
            builder.WithUrl(new Uri(uri, "_blazor/"));
            builder.ConfigureLogging(l => l.AddConsole().SetMinimumLevel(LogLevel.Trace));
            var hive = new ElementHive();

            HubConnection connection;
            await using (connection = builder.Build())
            {
                await connection.StartAsync(CancellationToken);
                Console.WriteLine("Connected");

                connection.On<int, string, string>("JS.BeginInvokeJS", OnBeginInvokeJS);
                connection.On<int, int, byte[]>("JS.RenderBatch", OnRenderBatch);
                connection.On<Error>("JS.OnError", OnError);
                connection.Closed += OnClosedAsync;

                // Now everything is registered so we can start the circuit.
                var success = await connection.InvokeAsync<bool>("ConnectCircuit", circuitId);

                await TaskCompletionSource.Task;

            }

            void OnBeginInvokeJS(int asyncHandle, string identifier, string argsJson)
            {
                Console.WriteLine("JS Invoke: " + identifier + " (" + argsJson + ")");
            }

            void OnRenderBatch(int browserRendererId, int batchId, byte[] batchData)
            {
                var batch = RenderBatchReader.Read(batchData);
                hive.Update(batch);
                Console.WriteLine("Hit enter to perform a click");

                if (!hive.TryFindElementById("thecounter", out var elementNode))
                {
                    Console.WriteLine("Could not find the counter to perform a click. Exiting.");
                    return;
                }

                if (!elementNode.Events.TryGetValue("click", out var clickEventDescriptor))
                {
                    Console.WriteLine("Button does not have a click event. Exiting");
                    return;
                }
                var mouseEventArgs = new UIMouseEventArgs()
                {
                    Type = clickEventDescriptor.EventName,
                    Detail = 1,
                    ScreenX = 0,
                    ScreenY = 0,
                    ClientX = 0,
                    ClientY = 0,
                    Button = 0,
                    Buttons = 0,
                    CtrlKey = false,
                    ShiftKey = false,
                    AltKey = false,
                    MetaKey = false
                };
                var browserDescriptor = new RendererRegistryEventDispatcher.BrowserEventDescriptor()
                {
                    BrowserRendererId = 0,
                    EventHandlerId = clickEventDescriptor.EventId,
                    EventArgsType = "mouse",
                };
                var serializedJson = JsonSerializer.ToString(mouseEventArgs, new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var argsObject = new object[] { browserDescriptor, serializedJson };
                var callId = "0";
                var assemblyName = "Microsoft.AspNetCore.Components.Browser";
                var methodIdentifier = "DispatchEvent";
                var dotNetObjectId = 0;
                var clickArgs = JsonSerializer.ToString(argsObject, new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                connection.InvokeAsync("BeginInvokeDotNetFromJS", callId, assemblyName, methodIdentifier, dotNetObjectId, clickArgs);

            }

            void OnError(Error error)
            {
                Console.WriteLine("ERROR: " + error.Stack);
            }

            Task OnClosedAsync(Exception ex)
            {
                if (ex == null)
                {
                    TaskCompletionSource.TrySetResult(null);
                }
                else
                {
                    TaskCompletionSource.TrySetException(ex);
                }

                return Task.CompletedTask;
            }
        }

        public void Cancel()
        {
            CancellationTokenSource.Cancel();
            CancellationTokenSource.Dispose();
        }

        private class Error
        {
            public string Stack { get; set; }
        }
    }
}
