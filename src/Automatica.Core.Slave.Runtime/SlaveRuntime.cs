﻿using Automatica.Core.Slave.Abstraction;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Diagnostics;
using MQTTnet.Server;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Client.Options;
using Timer = System.Timers.Timer;

namespace Automatica.Core.Slave.Runtime
{
    public class SlaveRuntime : IHostedService
    {
        private readonly IMqttClientOptions _options;
        private readonly IMqttClient _mqttClient;
        private readonly DockerClient _dockerClient;

        private readonly IDictionary<string, string> _runningImages = new Dictionary<string, string>();

        public string SlaveId => _slaveId;
        private readonly string _slaveId;

        private readonly string _masterAddress;
        private readonly string _clientKey;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);

        private readonly Timer _timer = new Timer(5000);

        public SlaveRuntime(IServiceProvider services, ILogger<SlaveRuntime> logger)
        {
            var config = services.GetRequiredService<IConfiguration>();

            _logger = logger;

            _masterAddress = config["server:master"];
            _clientKey = config["server:clientKey"];

            _slaveId = config["server:clientId"];
            _options = new MqttClientOptionsBuilder()
                   .WithClientId(_slaveId)
                   //   .WithWebSocketServer("localhost:5001/mqtt")
                   .WithTcpServer(_masterAddress, 1883)
                   .WithCredentials(_slaveId, _clientKey)
                   .WithCleanSession()
                   .Build();

            if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("MQTT_LOG_VERBOSE")))
            {
                MqttNetGlobalLogger.LogMessagePublished += (s, e) =>
                {
                    var trace =
                        $">> [{e.TraceMessage.Timestamp:O}] [{e.TraceMessage.ThreadId}] [{e.TraceMessage.Source}] [{e.TraceMessage.Level}]: {e.TraceMessage.Message}";
                    if (e.TraceMessage.Exception != null)
                    {
                        trace += Environment.NewLine + e.TraceMessage.Exception.ToString();
                    }

                    _logger.LogTrace(trace);
                };
            }

            _mqttClient = new MqttFactory().CreateMqttClient();
            _mqttClient.ApplicationMessageReceivedHandler = new ApplicationMessageReceivedHandler(this, _logger);

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    _dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _dockerClient = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();
                }
                else
                {
                    throw new PlatformNotSupportedException();
                }
            }
            catch (PlatformNotSupportedException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Could not connect do docker daemon!");
            }


            _timer.Elapsed += _timer_Elapsed;

        }

        private async void _timer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_mqttClient.IsConnected)
            {
                await StopInternal();
                await StartInternal();
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await StartInternal();
            _timer.Start();
        }

        private async Task StartInternal()
        {
            try
            {
                await _dockerClient.Images.ListImagesAsync(new ImagesListParameters());

                await _mqttClient.ConnectAsync(_options);

                var topic = $"slave/{_slaveId}/action";
                var topics = $"slave/{_slaveId}/actions";

                await _mqttClient.SubscribeAsync(new TopicFilterBuilder().WithTopic(topic).WithExactlyOnceQoS().Build());
                await _mqttClient.SubscribeAsync(new TopicFilterBuilder().WithTopic(topics).WithExactlyOnceQoS().Build());


                
            }
            catch (HttpRequestException e)
            {
                _logger.LogError(e, $"Error connecting to docker process...");
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error starting slave process...");
            }
        }

        internal async Task ExecuteAction(ActionRequest action)
        {
            switch (action.Action)
            {
                case SlaveAction.Start:
                    await StartImage(action);
                    break;
                case SlaveAction.Stop:
                    await StopImage(action);
                    break;
            }
        }

        private async Task StopImage(ActionRequest request)
        {
            var imageFullName = $"{request.ImageName}:{request.Tag}";
            _logger.LogInformation($"Stop Image {imageFullName}");

            if (_runningImages.ContainsKey(request.Id.ToString()))
            {
                await _dockerClient.Containers.StopContainerAsync(_runningImages[request.Id.ToString()], new ContainerStopParameters());
                try
                {
                    await _dockerClient.Images.DeleteImageAsync(imageFullName, new ImageDeleteParameters
                    {
                        Force = true
                    });
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Could not delete image");
                }

                _runningImages.Remove(request.Id.ToString());
            }
            else
            {
                _logger.LogError($"Could not stop image, image {imageFullName} not found");
            }
        }

        private async Task StartImage(ActionRequest request)
        {
            await _semaphore.WaitAsync();

            var imageFullName = $"{request.ImageName}:{request.Tag}";

            try
            {
                if (_runningImages.ContainsKey(request.Id.ToString()))
                {
                    _logger.LogWarning($"Image id {request.Id.ToString()} with image {imageFullName} already running, ignore now!");
                    return;
                }

                _logger.LogInformation($"Start Image {imageFullName}");

                var imageCreateParams = new ImagesCreateParameters()
                {
                    FromImage = request.ImageName,
                    Tag = request.Tag
                };

                if (!String.IsNullOrEmpty(request.ImageSource))
                {
                    imageCreateParams.FromSrc = request.ImageSource;
                }

                await _dockerClient.Images.CreateImageAsync(imageCreateParams, new AuthConfig(),
                    new ImageProgress(_logger));


                var portBindings = new Dictionary<string, IList<PortBinding>>();
                portBindings.Add("1883/tcp", new List<PortBinding>()
                {
                    new PortBinding()
                    {
                        HostPort = "1883"
                    }
                });

                var createContainerParams = new CreateContainerParameters()
                {
                    Image = imageFullName,
                    AttachStderr = false,
                    AttachStdin = false,
                    AttachStdout = false,
                    HostConfig = new HostConfig
                    {
                        PortBindings = portBindings
                    },
                    Env = new[]
                    {
                        $"AUTOMATICA_SLAVE_MASTER={_masterAddress}", $"AUTOMATICA_SLAVE_USER={_slaveId}",
                        $"AUTOMATICA_SLAVE_PASSWORD={_clientKey}", $"AUTOMATICA_NODE_ID={request.Id.ToString()}"
                    },
                };

                createContainerParams.HostConfig.Mounts = new List<Mount>();
                createContainerParams.HostConfig.NetworkMode = "host";

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                    RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    createContainerParams.HostConfig.Privileged = true;

                    createContainerParams.HostConfig.Mounts.Add(new Mount
                    {
                        Source = "/dev",
                        Target = "/dev",
                        Type = "bind"
                    });

                    createContainerParams.HostConfig.Mounts.Add(new Mount
                    {
                        Source = "/tmp",
                        Target = "/tmp",
                        Type = "bind"
                    });
                }

                var response = await _dockerClient.Containers.CreateContainerAsync(createContainerParams);

                _runningImages.Add(request.Id.ToString(), response.ID);
                await _dockerClient.Containers.StartContainerAsync(response.ID, new ContainerStartParameters());

            }
            catch (Exception e)
            {
                _logger.LogError(e, "error starting image...");
            }
            finally
            {
                _semaphore.Release(1);
            }

        }

        private async Task StopInternal()
        {
            try
            {
                foreach (var id in _runningImages)
                {
                    await _dockerClient.Containers.StopContainerAsync(id.Value, new ContainerStopParameters());
                }

                _runningImages.Clear();
                await _mqttClient.DisconnectAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, $"Error stopping connection...");
            }
        }


        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await StopInternal();


            _timer.Elapsed -= _timer_Elapsed;
        }
    }
}
