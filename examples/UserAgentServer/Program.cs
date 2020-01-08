﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example program of how to use the SIPSorcery core library to 
// act as the server for a SIP call.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 09 Oct 2019	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

//-----------------------------------------------------------------------------
// This example can be used with the automated SIP test tool [SIPp] (https://github.com/SIPp/sipp)
// and its inbuilt User Agent Client scenario.
// Note: IPp doesn't support IPv6.
//
// To isntall on WSL:
// $ sudo apt install sip-tester
//
// Running tests (press the '+' key while test is running to increase the call rate):
// For UDP testing: sipp -sn uac 127.0.0.1
// For TCP testing: sipp -sn uac localhost -t t1
//-----------------------------------------------------------------------------

//-----------------------------------------------------------------------------
// Media files:
// The "Simplicity" audio used in this example is from an artist called MACROFORM
// and can be downloaded directly from: https://www.jamendo.com/track/579315/simplicity?language=en
// The use of the audio is licensed under the Creative Commons 
// https://creativecommons.org/licenses/by-nd/2.0/
// The audio is free for personal use but a license may be required for commerical use.
// If it sounds familair this particular file is also included as part of Asterisk's 
// (asterisk.org) music on hold.
//
// ffmpeg can be used to convert the mp3 file into the required format for placing directly 
// into the RTP packets. Currently this example supports two audio formats: G711.ULAW (or PCMU)
// and G722.
//
// ffmpeg -i Macroform_-_Simplicity.mp3 -ac 1 -ar 8k -ab 64k -f mulaw Macroform_-_Simplicity.ulaw
// ffmpeg -i Macroform_-_Simplicity.mp3 -ar 16k -acodec g722 Macroform_-_Simplicity.g722
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;

namespace SIPSorcery
{
    class Program
    {
        private static readonly string AUDIO_FILE_PCMU = @"media\Macroform_-_Simplicity.ulaw";
        //private static readonly string AUDIO_FILE_MP3 = @"media\Macroform_-_Simplicity.mp3";
        private static readonly string AUDIO_FILE_G722 = @"media\Macroform_-_Simplicity.g722";

        private static int SIP_LISTEN_PORT = 5060;
        private static int SIPS_LISTEN_PORT = 5061;
        private static int SIP_WEBSOCKET_LISTEN_PORT = 80;
        private static int SIP_SECURE_WEBSOCKET_LISTEN_PORT = 443;

        private static WaveFormat _waveFormat = new WaveFormat(8000, 16, 1);  // PCMU format used by both input and output streams.
        private static int INPUT_SAMPLE_PERIOD_MILLISECONDS = 60;           // This sets the frequency of the RTP packets.

        private static Microsoft.Extensions.Logging.ILogger Log = SIPSorcery.Sys.Log.Logger;

        static void Main(string[] args)
        {
            Console.WriteLine("SIPSorcery user agent server example.");
            Console.WriteLine("Press h to hangup a call or ctrl-c to exit.");

            EnableConsoleLogger();

            IPAddress listenAddress = IPAddress.Any;
            IPAddress listenIPv6Address = IPAddress.IPv6Any;
            if (args != null && args.Length > 0)
            {
                if (!IPAddress.TryParse(args[0], out var customListenAddress))
                {
                    Log.LogWarning($"Command line argument could not be parsed as an IP address \"{args[0]}\"");
                    listenAddress = IPAddress.Any;
                }
                else
                {
                    if (customListenAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        listenAddress = customListenAddress;
                    }
                    if (customListenAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        listenIPv6Address = customListenAddress;
                    }
                }
            }

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport();

            var localhostCertificate = new X509Certificate2("localhost.pfx");

            // IPv4 channels.
            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(listenAddress, SIP_LISTEN_PORT)));
            sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(listenAddress, SIP_LISTEN_PORT)));
            sipTransport.AddSIPChannel(new SIPTLSChannel(localhostCertificate, new IPEndPoint(listenAddress, SIPS_LISTEN_PORT)));
            sipTransport.AddSIPChannel(new SIPWebSocketChannel(IPAddress.Any, SIP_WEBSOCKET_LISTEN_PORT));
            sipTransport.AddSIPChannel(new SIPWebSocketChannel(IPAddress.Any, SIP_SECURE_WEBSOCKET_LISTEN_PORT, localhostCertificate));

            // IPv6 channels.
            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(listenIPv6Address, SIP_LISTEN_PORT)));
            sipTransport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(listenIPv6Address, SIP_LISTEN_PORT)));
            sipTransport.AddSIPChannel(new SIPTLSChannel(localhostCertificate, new IPEndPoint(listenIPv6Address, SIPS_LISTEN_PORT)));
            sipTransport.AddSIPChannel(new SIPWebSocketChannel(IPAddress.IPv6Any, SIP_WEBSOCKET_LISTEN_PORT));
            sipTransport.AddSIPChannel(new SIPWebSocketChannel(IPAddress.IPv6Any, SIP_SECURE_WEBSOCKET_LISTEN_PORT, localhostCertificate));

            EnableTraceLogs(sipTransport);

            // To keep things a bit simpler this example only supports a single call at a time and the SIP server user agent
            // acts as a singleton
            SIPServerUserAgent uas = null;
            CancellationTokenSource rtpCts = null; // Cancellation token to stop the RTP stream.
            RTPSession rtpSession = null;

            // Because this is a server user agent the SIP transport must start listening for client user agents.
            sipTransport.SIPTransportRequestReceived += async (SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest) =>
            {
                try
                {
                    if (sipRequest.Method == SIPMethodsEnum.INVITE)
                    {
                        SIPSorcery.Sys.Log.Logger.LogInformation($"Incoming call request: {localSIPEndPoint}<-{remoteEndPoint} {sipRequest.URI}.");

                        // Check there's a codec we support in the INVITE offer.
                        var offerSdp = SDP.ParseSDPDescription(sipRequest.Body);
                        IPEndPoint dstRtpEndPoint = SDP.GetSDPRTPEndPoint(sipRequest.Body);
                        string audioFile = null;

                        if (offerSdp.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.HasMediaFormat((int)SDPMediaFormatsEnum.G722)))
                        {
                            Log.LogDebug($"Using G722 RTP media type and audio file {AUDIO_FILE_G722}.");
                            rtpSession = new RTPSession((int)SDPMediaFormatsEnum.G722, null, null, false, dstRtpEndPoint.AddressFamily);
                            audioFile = AUDIO_FILE_G722;
                        }
                        else if (offerSdp.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.HasMediaFormat((int)SDPMediaFormatsEnum.PCMU)))
                        {
                            Log.LogDebug($"Using PCMU RTP media type and audio file {AUDIO_FILE_PCMU}.");
                            rtpSession = new RTPSession((int)SDPMediaFormatsEnum.PCMU, null, null, false, dstRtpEndPoint.AddressFamily);
                            audioFile = AUDIO_FILE_PCMU;
                        }

                        if (rtpSession == null)
                        {
                            // Didn't get a match on the codecs we support.
                            SIPResponse noMatchingCodecResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.NotAcceptableHere, null);
                            await sipTransport.SendResponseAsync(noMatchingCodecResponse);
                        }
                        else
                        {
                            // If there's already a call in progress hang it up. Of course this is not ideal for a real softphone or server but it 
                            // means this example can be kept simpler.
                            if (uas?.IsHungup == false)
                            {
                                uas?.Hangup(false);
                            }
                            rtpCts?.Cancel();
                            rtpCts = new CancellationTokenSource();

                            UASInviteTransaction uasTransaction = new UASInviteTransaction(sipTransport, sipRequest, null);
                            uas = new SIPServerUserAgent(sipTransport, null, null, null, SIPCallDirection.In, null, null, null, uasTransaction);
                            uas.CallCancelled += (uasAgent) => 
                            {
                                rtpCts?.Cancel();
                                rtpSession.CloseSession(null);
                            };
                            rtpSession.OnRtpClosed += (reason) => uas?.Hangup(false);
                            uas.Progress(SIPResponseStatusCodesEnum.Trying, null, null, null, null);
                            uas.Progress(SIPResponseStatusCodesEnum.Ringing, null, null, null, null);

                            // The RTP socket is listening on IPAddress.Any but the IP address placed into the SDP needs to be one the caller can reach.
                            IPAddress rtpAddress = NetServices.GetLocalAddressForRemote(dstRtpEndPoint.Address);

                            // Only set the remote RTP end point if there hasn't already been a packet received on it.
                            if (rtpSession.DestinationEndPoint == null)
                            {
                                rtpSession.DestinationEndPoint = SDP.GetSDPRTPEndPoint(sipRequest.Body);
                                Log.LogDebug($"Remote RTP socket {rtpSession.DestinationEndPoint}.");
                            }

                            rtpSession.SetRemoteSDP(SDP.ParseSDPDescription(sipRequest.Body));

                            _ = Task.Run(() => SendRtp(rtpSession, dstRtpEndPoint, audioFile, rtpCts))
                                .ContinueWith(_ => 
                                {
                                    if (uas?.IsHungup == false)
                                    {
                                        uas?.Hangup(false);
                                        rtpSession?.CloseSession(null);
                                    }
                                });

                            uas.Answer(SDP.SDP_MIME_CONTENTTYPE, rtpSession.GetSDP(rtpAddress).ToString(), null, SIPDialogueTransferModesEnum.NotAllowed);
                        }
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.BYE)
                    {
                        SIPSorcery.Sys.Log.Logger.LogInformation("Call hungup.");
                        SIPResponse byeResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                        await sipTransport.SendResponseAsync(byeResponse);
                        uas?.Hangup(true);
                        rtpSession?.CloseSession(null);
                        rtpCts?.Cancel();
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.SUBSCRIBE)
                    {
                        SIPResponse notAllowededResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                        await sipTransport.SendResponseAsync(notAllowededResponse);
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.OPTIONS || sipRequest.Method == SIPMethodsEnum.REGISTER)
                    {
                        SIPResponse optionsResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                        await sipTransport.SendResponseAsync(optionsResponse);
                    }
                }
                catch (Exception reqExcp)
                {
                    SIPSorcery.Sys.Log.Logger.LogWarning($"Exception handling {sipRequest.Method}. {reqExcp.Message}");
                }
            };

            ManualResetEvent exitMre = new ManualResetEvent(false);

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;

                SIPSorcery.Sys.Log.Logger.LogInformation("Exiting...");

                Hangup(uas).Wait();

                rtpSession?.CloseSession(null);
                rtpCts?.Cancel();

                if (sipTransport != null)
                {
                    SIPSorcery.Sys.Log.Logger.LogInformation("Shutting down SIP transport...");
                    sipTransport.Shutdown();
                }

                exitMre.Set();
            };

            // Task to handle user key presses.
            Task.Run(() =>
            {
                try
                {
                    while (!exitMre.WaitOne(0))
                    {
                        var keyProps = Console.ReadKey();
                        if (keyProps.KeyChar == 'h' || keyProps.KeyChar == 'q')
                        {
                            Console.WriteLine();
                            Console.WriteLine("Hangup requested by user...");

                            Hangup(uas).Wait();

                            rtpSession?.CloseSession(null);
                            rtpCts?.Cancel();
                        }

                        if (keyProps.KeyChar == 'q')
                        {
                            SIPSorcery.Sys.Log.Logger.LogInformation("Quitting...");

                            if (sipTransport != null)
                            {
                                SIPSorcery.Sys.Log.Logger.LogInformation("Shutting down SIP transport...");
                                sipTransport.Shutdown();
                            }

                            exitMre.Set();
                        }
                    }
                }
                catch (Exception excp)
                {
                    SIPSorcery.Sys.Log.Logger.LogError($"Exception Key Press listener. {excp.Message}.");
                }
            });

            exitMre.WaitOne();
        }

        private static async Task SendRtp(RTPSession rtpSession, IPEndPoint dstRtpEndPoint, string audioFileName, CancellationTokenSource cts)
        {
            try
            {
                string audioFileExt = Path.GetExtension(audioFileName).ToLower();

                switch (audioFileExt)
                {
                    case ".g722":
                    case ".ulaw":
                        {
                            uint timestamp = 0;
                            using (StreamReader sr = new StreamReader(audioFileName))
                            {
                                byte[] buffer = new byte[320];
                                int bytesRead = sr.BaseStream.Read(buffer, 0, buffer.Length);

                                while (bytesRead > 0 && !cts.IsCancellationRequested)
                                {
                                    if (!dstRtpEndPoint.Address.Equals(IPAddress.Any))
                                    {
                                        rtpSession.SendAudioFrame(timestamp, buffer);
                                    }

                                    timestamp += (uint)buffer.Length;

                                    await Task.Delay(40, cts.Token);
                                    bytesRead = sr.BaseStream.Read(buffer, 0, buffer.Length);
                                }
                            }
                        }
                        break;

                    case ".mp3":
                        {
                            var pcmFormat = new WaveFormat(8000, 16, 1);
                            var ulawFormat = WaveFormat.CreateMuLawFormat(8000, 1);

                            uint timestamp = 0;

                            using (WaveFormatConversionStream pcmStm = new WaveFormatConversionStream(pcmFormat, new Mp3FileReader(audioFileName)))
                            {
                                using (WaveFormatConversionStream ulawStm = new WaveFormatConversionStream(ulawFormat, pcmStm))
                                {
                                    byte[] buffer = new byte[320];
                                    int bytesRead = ulawStm.Read(buffer, 0, buffer.Length);

                                    while (bytesRead > 0 && !cts.IsCancellationRequested)
                                    {
                                        byte[] sample = new byte[bytesRead];
                                        Array.Copy(buffer, sample, bytesRead);

                                        if (dstRtpEndPoint.Address != IPAddress.Any)
                                        {
                                            rtpSession.SendAudioFrame(timestamp, buffer);
                                        }

                                        timestamp += (uint)buffer.Length;

                                        await Task.Delay(40, cts.Token);
                                        bytesRead = ulawStm.Read(buffer, 0, buffer.Length);
                                    }
                                }
                            }
                        }
                        break;

                    default:
                        throw new NotImplementedException($"The {audioFileExt} file type is not understood by this example.");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception excp)
            {
                SIPSorcery.Sys.Log.Logger.LogError($"Exception sending RTP. {excp.Message}");
            }
        }

        private static SDP GetSDP(IPEndPoint rtpSocket)
        {
            var sdp = new SDP(rtpSocket.Address)
            {
                SessionId = Crypto.GetRandomInt(5).ToString(),
                SessionName = "sipsorcery",
                Timing = "0 0",
                Connection = new SDPConnectionInformation(rtpSocket.Address),
            };

            var audioAnnouncement = new SDPMediaAnnouncement()
            {
                Media = SDPMediaTypesEnum.audio,
                MediaFormats = new List<SDPMediaFormat>() { new SDPMediaFormat((int)SDPMediaFormatsEnum.PCMU, "PCMU", 8000),
                                                            new SDPMediaFormat((int)SDPMediaFormatsEnum.G722, "G722", 8000) }
            };
            audioAnnouncement.Port = rtpSocket.Port;
            audioAnnouncement.MediaStreamStatus = MediaStreamStatusEnum.SendRecv;
            sdp.Media.Add(audioAnnouncement);

            return sdp;
        }

        /// <summary>
        /// Get the audio input device, e.g. microphone. The input device that will provide 
        /// audio samples that can be encoded, packaged into RTP and sent to the remote call party.
        /// </summary>
        private static WaveInEvent GetAudioInputDevice()
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                throw new ApplicationException("No audio input devices available. No audio will be sent.");
            }
            else
            {
                WaveInEvent waveInEvent = new WaveInEvent();
                WaveFormat waveFormat = _waveFormat;
                waveInEvent.BufferMilliseconds = INPUT_SAMPLE_PERIOD_MILLISECONDS;
                waveInEvent.NumberOfBuffers = 1;
                waveInEvent.DeviceNumber = 0;
                waveInEvent.WaveFormat = waveFormat;

                return waveInEvent;
            }
        }

        /// <summary>
        /// Hangs up the current call.
        /// </summary>
        /// <param name="uas">The user agent server to hangup the call on.</param>
        private static async Task Hangup(SIPServerUserAgent uas)
        {
            try
            {
                if (uas?.IsHungup == false)
                {
                    uas?.Hangup(false);

                    // Give the BYE or CANCEL request time to be transmitted.
                    SIPSorcery.Sys.Log.Logger.LogInformation("Waiting 1s for call to hangup...");
                    await Task.Delay(1000);
                }
            }
            catch (Exception excp)
            {
                SIPSorcery.Sys.Log.Logger.LogError($"Exception Hangup. {excp.Message}");
            }
        }

        /// <summary>
        /// Wires up the dotnet logging infrastructure to STDOUT.
        /// </summary>
        private static void EnableConsoleLogger()
        {
            // Logging configuration. Can be ommitted if internal SIPSorcery debug and warning messages are not required.
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;
        }

        /// <summary>
        /// Enable detailed SIP log messages.
        /// </summary>
        private static void EnableTraceLogs(SIPTransport sipTransport)
        {
            sipTransport.SIPRequestInTraceEvent += (localEP, remoteEP, req) =>
            {
                Log.LogDebug($"Request received: {localEP}<-{remoteEP}");
                Log.LogDebug(req.ToString());
            };

            sipTransport.SIPRequestOutTraceEvent += (localEP, remoteEP, req) =>
            {
                Log.LogDebug($"Request sent: {localEP}->{remoteEP}");
                Log.LogDebug(req.ToString());
            };

            sipTransport.SIPResponseInTraceEvent += (localEP, remoteEP, resp) =>
            {
                Log.LogDebug($"Response received: {localEP}<-{remoteEP}");
                Log.LogDebug(resp.ToString());
            };

            sipTransport.SIPResponseOutTraceEvent += (localEP, remoteEP, resp) =>
            {
                Log.LogDebug($"Response sent: {localEP}->{remoteEP}");
                Log.LogDebug(resp.ToString());
            };

            sipTransport.SIPRequestRetransmitTraceEvent += (tx, req, count) =>
            {
                Log.LogDebug($"Request retransmit {count} for request {req.StatusLine}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };

            sipTransport.SIPResponseRetransmitTraceEvent += (tx, resp, count) =>
            {
                Log.LogDebug($"Response retransmit {count} for response {resp.ShortDescription}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };
        }
    }
}
