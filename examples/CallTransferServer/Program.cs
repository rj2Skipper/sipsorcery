﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An example of using a REFER request to transfer a  received call.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// 28 Nov 2019	Aaron Clauson   Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        private static readonly int RTP_REPORTING_PERIOD_SECONDS = 5;       // Period at which to write RTP stats.
        private static int SIP_LISTEN_PORT = 5060;
        private static int RTP_PORT_START = 49000;
        private static int RTP_PORT_END = 49100;

        private static Microsoft.Extensions.Logging.ILogger Log = SIPSorcery.Sys.Log.Logger;

        static void Main()
        {
            Console.WriteLine("SIPSorcery client user agent example.");
            Console.WriteLine("Press ctrl-c to exit.");

            CancellationTokenSource exitCts = new CancellationTokenSource(); // Cancellation token to stop the SIP transport and RTP stream.

            AddConsoleLogger();

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport();
            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, SIP_LISTEN_PORT)));

            // Un/comment this line to see/hide each SIP message sent and received.
            EnableTraceLogs(sipTransport);

            // To keep things a bit simpler this example only supports a single call at a time and the SIP server user agent
            // acts as a singleton
            SIPUserAgent userAgent = new SIPUserAgent(sipTransport, null);
            CancellationTokenSource rtpCts = null; // Cancellation token to stop the RTP stream.
            Socket rtpSocket = null;
            Socket controlSocket = null;

            // Because this is a server user agent the SIP transport must start listening for client user agents.
            sipTransport.SIPTransportRequestReceived += (SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest) =>
            {
                try
                {
                    if (sipRequest.Header.From != null &&
                        sipRequest.Header.From.FromTag != null &&
                        sipRequest.Header.To != null &&
                        sipRequest.Header.To.ToTag != null)
                    {
                        userAgent.InDialogRequestReceivedAsync(sipRequest).Wait();
                    }
                    if (sipRequest.Method == SIPMethodsEnum.INVITE)
                    {
                        SIPSorcery.Sys.Log.Logger.LogInformation($"Incoming call request: {localSIPEndPoint}<-{remoteEndPoint} {sipRequest.URI}.");

                        // Check there's a codec we support in the INVITE offer.
                        var offerSdp = SDP.ParseSDPDescription(sipRequest.Body);
                        IPEndPoint dstRtpEndPoint = SDP.GetSDPRTPEndPoint(sipRequest.Body);
                        RTPSession rtpSession = null;
                        string audioFile = null;

                        if (offerSdp.Media.Any(x => x.Media == SDPMediaTypesEnum.audio && x.HasMediaFormat((int)RTPPayloadTypesEnum.PCMU)))
                        {
                            Log.LogDebug($"Using PCMU RTP media type and audio file {AUDIO_FILE_PCMU}.");
                            rtpSession = new RTPSession((int)RTPPayloadTypesEnum.PCMU, null, null);
                            audioFile = AUDIO_FILE_PCMU;
                        }

                        if (rtpSession == null)
                        {
                            // Didn't get a match on the codecs we support.
                            SIPResponse noMatchingCodecResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.NotAcceptableHere, null);
                            sipTransport.SendResponse(noMatchingCodecResponse);
                        }
                        else
                        {
                            // If there's already a call in progress hang it up. Of course this is not ideal for a real softphone or server but it 
                            // means this example can be kept simpler.
                            if (userAgent?.IsAnswered == true)
                            {
                                userAgent?.Hangup();
                            }
                            rtpCts?.Cancel();

                            UASInviteTransaction uasTransaction = sipTransport.CreateUASTransaction(sipRequest, null);
                            if (userAgent.AcceptCall(uasTransaction))
                            {
                                rtpCts = new CancellationTokenSource();

                                // The RTP socket is listening on IPAddress.Any but the IP address placed into the SDP needs to be one the caller can reach.
                                IPAddress rtpAddress = NetServices.GetLocalAddressForRemote(dstRtpEndPoint.Address);
                                // Initialise an RTP session to receive the RTP packets from the remote SIP server.
                                NetServices.CreateRtpSocket(rtpAddress, RTP_PORT_START, RTP_PORT_END, false, out rtpSocket, out controlSocket);

                                var rtpRecvSession = new RTPSession((int)RTPPayloadTypesEnum.PCMU, null, null);
                                var rtpSendSession = new RTPSession((int)RTPPayloadTypesEnum.PCMU, null, null);
                                rtpSendSession.DestinationEndPoint = dstRtpEndPoint;
                                rtpRecvSession.OnReceiveFromEndPointChanged += (oldEP, newEP) => 
                                {
                                    Log.LogDebug($"RTP destination end point changed from {oldEP} to {newEP}.");
                                    rtpSendSession.DestinationEndPoint = newEP;
                                };

                                Task.Run(() => RecvRtp(rtpSocket, rtpRecvSession, rtpCts));
                                Task.Run(() => SendRtp(rtpSocket, rtpSendSession, rtpCts));

                                userAgent.Answer(GetSDP(rtpSocket.LocalEndPoint as IPEndPoint));
                            }
                        }
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.SUBSCRIBE)
                    {
                        SIPResponse notAllowededResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.MethodNotAllowed, null);
                        sipTransport.SendResponse(notAllowededResponse);
                    }
                    else if (sipRequest.Method == SIPMethodsEnum.OPTIONS || sipRequest.Method == SIPMethodsEnum.REGISTER)
                    {
                        SIPResponse optionsResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                        sipTransport.SendResponse(optionsResponse);
                    }
                }
                catch (Exception reqExcp)
                {
                    SIPSorcery.Sys.Log.Logger.LogWarning($"Exception handling {sipRequest.Method}. {reqExcp.Message}");
                }
            };

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitCts.Cancel();
                rtpCts?.Cancel();
            };

            // Wait for a signal saying the call failed, was cancelled with ctrl-c or completed.
            exitCts.Token.WaitHandle.WaitOne();

            Log.LogInformation("Exiting...");

            rtpSocket?.Close();
            controlSocket?.Close();

            if (userAgent != null)
            {
                if (userAgent.IsAnswered)
                {
                    Log.LogInformation($"Hanging up call to {userAgent?.CallDescriptor?.To}.");
                    userAgent.Hangup();
                }

                // Give the final request time to be transmitted.
                Log.LogInformation("Waiting 1s for call to clean up...");
                Task.Delay(1000).Wait();
            }

            SIPSorcery.Net.DNSManager.Stop();

            if (sipTransport != null)
            {
                Log.LogInformation("Shutting down SIP transport...");
                sipTransport.Shutdown();
            }
        }

        /// <summary>
        ///  Adds a console logger. Can be ommitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static void AddConsoleLogger()
        {
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
        /// Handling packets received on the RTP socket. One of the simplest, if not the simplest, cases is
        /// PCMU audio packets. THe handling can get substantially more complicated if the RTP socket is being
        /// used to multiplex different protocols. This is what WebRTC does with STUN, RTP and RTCP.
        /// </summary>
        /// <param name="rtpSocket">The raw RTP socket.</param>
        /// <param name="rtpSendSession">The session infor for the RTP pakcets being sent.</param>
        private static async void RecvRtp(Socket rtpSocket, RTPSession rtpRecvSession, CancellationTokenSource cts)
        {
            try
            {
                DateTime lastRecvReportAt = DateTime.Now;
                uint packetReceivedCount = 0;
                uint bytesReceivedCount = 0;
                byte[] buffer = new byte[512];

                IPEndPoint anyEndPoint = new IPEndPoint((rtpSocket.AddressFamily == AddressFamily.InterNetworkV6) ? IPAddress.IPv6Any : IPAddress.Any, 0);

                Log.LogDebug($"Listening on RTP socket {rtpSocket.LocalEndPoint}.");

                using (var waveOutEvent = new WaveOutEvent())
                {
                    var waveProvider = new BufferedWaveProvider(new WaveFormat(8000, 16, 1));
                    waveProvider.DiscardOnBufferOverflow = true;
                    waveOutEvent.Init(waveProvider);
                    waveOutEvent.Play();

                    var recvResult = await rtpSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint);

                    Log.LogDebug($"Initial RTP packet recieved from {recvResult.RemoteEndPoint}.");

                    while (recvResult.ReceivedBytes > 0 && !cts.IsCancellationRequested)
                    {
                        var rtpPacket = rtpRecvSession.RtpReceive(buffer, 0, recvResult.ReceivedBytes, recvResult.RemoteEndPoint as IPEndPoint);

                        packetReceivedCount++;
                        bytesReceivedCount += (uint)rtpPacket.Payload.Length;

                        for (int index = 0; index < rtpPacket.Payload.Length; index++)
                        {
                            short pcm = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(rtpPacket.Payload[index]);
                            byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
                            waveProvider.AddSamples(pcmSample, 0, 2);
                        }

                        if (DateTime.Now.Subtract(lastRecvReportAt).TotalSeconds > RTP_REPORTING_PERIOD_SECONDS)
                        {
                            // This is typically where RTCP receiver (RR) reports would be sent. Omitted here for brevity.
                            lastRecvReportAt = DateTime.Now;
                            var remoteRtpEndPoint = recvResult.RemoteEndPoint as IPEndPoint;
                            Log.LogDebug($"RTP recv report {rtpSocket.LocalEndPoint}<-{remoteRtpEndPoint} pkts {packetReceivedCount} bytes {bytesReceivedCount}");
                        }

                        recvResult = await rtpSocket.ReceiveFromAsync(buffer, SocketFlags.None, anyEndPoint);
                    }
                }
            }
            catch (ObjectDisposedException) { } // This is how .Net deals with an in use socket being closed. Safe to ignore.
            catch (Exception excp)
            {
                Log.LogError($"Exception processing RTP. {excp}");
            }
        }

        /// <summary>
        /// Sends the sounds of silence. If the destination is on the other side of a NAT this is useful to open
        /// a pinhole and hopefully get the remote RTP stream through.
        /// </summary>
        /// <param name="rtpSocket">The socket we're using to send from.</param>
        /// <param name="rtpSendSession">Our RTP sending session.</param>
        /// <param name="cts">Cancellation token to stop the call.</param>
        private static async void SendRtp(Socket rtpSocket, RTPSession rtpSendSession, CancellationTokenSource cts)
        {
            while (cts.IsCancellationRequested == false)
            {
                uint timestamp = 0;
                using (StreamReader sr = new StreamReader(AUDIO_FILE_PCMU))
                {
                    DateTime lastSendReportAt = DateTime.Now;
                    uint packetsSentCount = 0;
                    uint bytesSentCount = 0;
                    byte[] buffer = new byte[320];
                    int bytesRead = sr.BaseStream.Read(buffer, 0, buffer.Length);

                    while (bytesRead > 0 && !cts.IsCancellationRequested)
                    {
                        if (rtpSendSession.DestinationEndPoint != null)
                        {
                            packetsSentCount++;
                            bytesSentCount += (uint)bytesRead;
                            rtpSendSession.SendAudioFrame(rtpSocket, rtpSendSession.DestinationEndPoint, timestamp, buffer);
                        }

                        timestamp += (uint)buffer.Length;

                        if (DateTime.Now.Subtract(lastSendReportAt).TotalSeconds > RTP_REPORTING_PERIOD_SECONDS)
                        {
                            lastSendReportAt = DateTime.Now;
                            SIPSorcery.Sys.Log.Logger.LogDebug($"RTP send report {rtpSocket.LocalEndPoint}->{rtpSendSession.DestinationEndPoint} pkts {packetsSentCount} bytes {bytesSentCount}");
                        }

                        await Task.Delay(40, cts.Token);
                        bytesRead = sr.BaseStream.Read(buffer, 0, buffer.Length);
                    }
                }
            }
        }

        /// <summary>
        /// Get the SDP payload for an INVITE request.
        /// </summary>
        /// <param name="rtpSocket">The RTP socket end point that will be used to receive and send RTP.</param>
        /// <returns>An SDP object.</returns>
        private static SDP GetSDP(IPEndPoint rtpSocket)
        {
            var sdp = new SDP()
            {
                SessionId = Crypto.GetRandomInt(5).ToString(),
                Address = rtpSocket.Address.ToString(),
                SessionName = "sipsorcery",
                Timing = "0 0",
                Connection = new SDPConnectionInformation(rtpSocket.Address.ToString()),
            };

            var audioAnnouncement = new SDPMediaAnnouncement()
            {
                Media = SDPMediaTypesEnum.audio,
                MediaFormats = new List<SDPMediaFormat>() { new SDPMediaFormat((int)SDPMediaFormatsEnum.PCMU, "PCMU", 8000) }
            };
            audioAnnouncement.Port = rtpSocket.Port;
            audioAnnouncement.ExtraAttributes.Add("a=sendrecv");
            sdp.Media.Add(audioAnnouncement);

            return sdp;
        }

        /// <summary>
        /// Builds the REFER request to transfer an established call.
        /// </summary>
        /// <param name="sipDialogue">A SIP dialogue object representing the established call.</param>
        /// <param name="referToUri">The SIP URI to transfer the call to.</param>
        /// <returns>A SIP REFER request.</returns>
        private static SIPRequest GetReferRequest(SIPClientUserAgent uac, SIPURI referToUri)
        {
            SIPDialogue sipDialogue = uac.SIPDialogue;

            SIPRequest referRequest = new SIPRequest(SIPMethodsEnum.REFER, sipDialogue.RemoteTarget);
            referRequest.SetSendFromHints(uac.ServerTransaction.TransactionRequest.LocalSIPEndPoint);

            SIPFromHeader referFromHeader = SIPFromHeader.ParseFromHeader(sipDialogue.LocalUserField.ToString());
            SIPToHeader referToHeader = SIPToHeader.ParseToHeader(sipDialogue.RemoteUserField.ToString());
            int cseq = sipDialogue.CSeq + 1;
            sipDialogue.CSeq++;

            SIPHeader referHeader = new SIPHeader(referFromHeader, referToHeader, cseq, sipDialogue.CallId);
            referHeader.CSeqMethod = SIPMethodsEnum.REFER;
            referRequest.Header = referHeader;
            referRequest.Header.Routes = sipDialogue.RouteSet;
            referRequest.Header.ProxySendFrom = sipDialogue.ProxySendFrom;
            referRequest.Header.Vias.PushViaHeader(SIPViaHeader.GetDefaultSIPViaHeader());
            referRequest.Header.ReferTo = referToUri.ToString();
            referRequest.Header.Contact = new List<SIPContactHeader>() { SIPContactHeader.GetDefaultSIPContactHeader() };

            return referRequest;
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
