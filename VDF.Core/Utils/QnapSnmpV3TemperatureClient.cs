// /*
//     Copyright (C) 2026 0x90d
//     This file is part of VideoDuplicateFinder
//     VideoDuplicateFinder is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published by
//     the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
// */

using System.Formats.Asn1;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace VDF.Core.Utils {

	internal interface IDiskTemperatureSource {
		Task<IReadOnlyDictionary<int, int>> GetTemperaturesAsync(IReadOnlyCollection<int> diskSlots, CancellationToken token);
	}

	/// <summary>
	/// Minimal SNMPv3 noAuthNoPriv client for QNAP's disk-temperature table. Keeping the
	/// implementation in-tree avoids requiring Python/net-snmp or another native/runtime
	/// dependency in the self-contained Native-AOT GUI build. Each poll performs USM engine
	/// discovery followed by one multi-varbind GET, so engine boots/time never go stale.
	/// </summary>
	internal sealed class QnapSnmpV3TemperatureClient : IDiskTemperatureSource {
		internal const string DiskTemperatureOid = "1.3.6.1.4.1.55062.1.10.2.1.8";
		const string DiscoveryOid = "1.3.6.1.2.1.1.1.0";
		static readonly Asn1Tag GetRequestTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
		static readonly Asn1Tag ResponseTag = new(TagClass.ContextSpecific, 2, isConstructed: true);
		static readonly Asn1Tag ReportTag = new(TagClass.ContextSpecific, 8, isConstructed: true);

		readonly string host;
		readonly int port;
		readonly string userName;
		readonly TimeSpan timeout;

		internal QnapSnmpV3TemperatureClient(string host, int port, string userName, TimeSpan? timeout = null) {
			this.host = host;
			this.port = port;
			this.userName = userName;
			this.timeout = timeout ?? TimeSpan.FromSeconds(4);
		}

		public async Task<IReadOnlyDictionary<int, int>> GetTemperaturesAsync(IReadOnlyCollection<int> diskSlots, CancellationToken token) {
			if (diskSlots.Count == 0)
				return new Dictionary<int, int>();

			SnmpEngineParameters engine = await DiscoverEngineAsync(token);
			string[] requestedOids = diskSlots
				.Distinct()
				.OrderBy(x => x)
				.Select(slot => $"{DiskTemperatureOid}.{slot}")
				.ToArray();
			int requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
			byte[] request = BuildMessage(engine.EngineId, engine.EngineBoots, engine.EngineTime,
				userName, engine.EngineId, requestedOids, requestId);
			byte[] response = await ExchangeAsync(request, token);
			Dictionary<string, int> values = ParseIntegerResponse(response, requestId);

			var result = new Dictionary<int, int>();
			foreach (int slot in diskSlots.Distinct()) {
				if (values.TryGetValue($"{DiskTemperatureOid}.{slot}", out int value))
					result[slot] = value;
			}
			return result;
		}

		async Task<SnmpEngineParameters> DiscoverEngineAsync(CancellationToken token) {
			int requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
			byte[] request = BuildMessage(Array.Empty<byte>(), 0, 0, string.Empty,
				Array.Empty<byte>(), new[] { DiscoveryOid }, requestId);
			byte[] response = await ExchangeAsync(request, token);
			SnmpEngineParameters parameters = ParseEngineParameters(response);
			if (parameters.EngineId.Length == 0)
				throw new InvalidDataException("SNMPv3 engine discovery returned an empty engine ID.");
			return parameters;
		}

		async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken token) {
			using var udp = new UdpClient();
			udp.Connect(host, port);
			using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeoutCts.CancelAfter(timeout);
			try {
				await udp.SendAsync(request.AsMemory(), timeoutCts.Token);
				UdpReceiveResult result = await udp.ReceiveAsync(timeoutCts.Token);
				return result.Buffer;
			}
			catch (OperationCanceledException) when (!token.IsCancellationRequested) {
				throw new TimeoutException($"No SNMP response from {host}:{port} within {timeout.TotalSeconds:0.#} seconds.");
			}
		}

		internal static byte[] BuildMessage(byte[] authoritativeEngineId, int engineBoots, int engineTime,
			string userName, byte[] contextEngineId, IReadOnlyList<string> oids, int requestId) {
			int messageId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
			byte[] securityParameters = BuildUsmSecurityParameters(authoritativeEngineId, engineBoots, engineTime, userName);
			var writer = new AsnWriter(AsnEncodingRules.BER);
			writer.PushSequence();
			writer.WriteInteger(3); // SNMPv3
			writer.PushSequence();
			writer.WriteInteger(messageId);
			writer.WriteInteger(65507);
			writer.WriteOctetString(new byte[] { 0x04 }); // reportable, noAuthNoPriv
			writer.WriteInteger(3); // USM
			writer.PopSequence();
			writer.WriteOctetString(securityParameters);

			writer.PushSequence(); // plaintext ScopedPDU
			writer.WriteOctetString(contextEngineId);
			writer.WriteOctetString(Array.Empty<byte>()); // default context
			writer.PushSequence(GetRequestTag);
			writer.WriteInteger(requestId);
			writer.WriteInteger(0);
			writer.WriteInteger(0);
			writer.PushSequence(); // variable bindings
			foreach (string oid in oids) {
				writer.PushSequence();
				writer.WriteObjectIdentifier(oid);
				writer.WriteNull();
				writer.PopSequence();
			}
			writer.PopSequence();
			writer.PopSequence(GetRequestTag);
			writer.PopSequence();
			writer.PopSequence();
			return writer.Encode();
		}

		static byte[] BuildUsmSecurityParameters(byte[] engineId, int engineBoots, int engineTime, string userName) {
			var writer = new AsnWriter(AsnEncodingRules.BER);
			writer.PushSequence();
			writer.WriteOctetString(engineId);
			writer.WriteInteger(engineBoots);
			writer.WriteInteger(engineTime);
			writer.WriteOctetString(System.Text.Encoding.UTF8.GetBytes(userName));
			writer.WriteOctetString(Array.Empty<byte>()); // auth parameters
			writer.WriteOctetString(Array.Empty<byte>()); // privacy parameters
			writer.PopSequence();
			return writer.Encode();
		}

		internal static SnmpEngineParameters ParseEngineParameters(byte[] response) {
			AsnReader message = ReadMessageSequence(response);
			_ = ReadHeader(message);
			byte[] encodedSecurity = message.ReadOctetString();
			var securityReader = new AsnReader(encodedSecurity, AsnEncodingRules.BER);
			AsnReader security = securityReader.ReadSequence();
			byte[] engineId = security.ReadOctetString();
			int boots = checked((int)security.ReadInteger());
			int time = checked((int)security.ReadInteger());
			return new SnmpEngineParameters(engineId, boots, time);
		}

		internal static Dictionary<string, int> ParseIntegerResponse(byte[] response, int expectedRequestId) {
			AsnReader message = ReadMessageSequence(response);
			_ = ReadHeader(message);
			_ = message.ReadOctetString(); // USM security parameters
			AsnReader scopedPdu = message.ReadSequence();
			_ = scopedPdu.ReadOctetString(); // context engine ID
			_ = scopedPdu.ReadOctetString(); // context name
			Asn1Tag pduTag = scopedPdu.PeekTag();
			if (!pduTag.HasSameClassAndValue(ResponseTag)) {
				if (pduTag.HasSameClassAndValue(ReportTag))
					throw new InvalidDataException("SNMPv3 agent returned a report instead of a response; verify the SNMPv3 user/security level.");
				throw new InvalidDataException($"Unexpected SNMP PDU tag {pduTag}.");
			}
			AsnReader pdu = scopedPdu.ReadSequence(ResponseTag);
			int requestId = checked((int)pdu.ReadInteger());
			if (requestId != expectedRequestId)
				throw new InvalidDataException("SNMP response request ID does not match the request.");
			int errorStatus = checked((int)pdu.ReadInteger());
			int errorIndex = checked((int)pdu.ReadInteger());
			if (errorStatus != 0)
				throw new InvalidDataException($"SNMP agent returned error status {errorStatus} at index {errorIndex}.");

			AsnReader bindings = pdu.ReadSequence();
			var result = new Dictionary<string, int>(StringComparer.Ordinal);
			while (bindings.HasData) {
				AsnReader binding = bindings.ReadSequence();
				string oid = binding.ReadObjectIdentifier();
				Asn1Tag valueTag = binding.PeekTag();
				if (valueTag.TagClass == TagClass.Universal && valueTag.TagValue == (int)UniversalTagNumber.Integer) {
					result[oid] = checked((int)binding.ReadInteger());
				}
				else if (valueTag.TagClass == TagClass.Universal && valueTag.TagValue == (int)UniversalTagNumber.OctetString) {
					string text = System.Text.Encoding.ASCII.GetString(binding.ReadOctetString()).Trim();
					if (int.TryParse(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out int parsed))
						result[oid] = parsed;
				}
				else {
					_ = binding.ReadEncodedValue(); // unsupported type; leave missing
				}
			}
			return result;
		}

		static AsnReader ReadMessageSequence(byte[] response) {
			var reader = new AsnReader(response, AsnEncodingRules.BER);
			AsnReader message = reader.ReadSequence();
			int version = checked((int)message.ReadInteger());
			if (version != 3)
				throw new InvalidDataException($"Expected SNMPv3 response, got version {version}.");
			return message;
		}

		static int ReadHeader(AsnReader message) {
			AsnReader header = message.ReadSequence();
			int messageId = checked((int)header.ReadInteger());
			_ = header.ReadInteger(); // max size
			_ = header.ReadOctetString(); // flags
			int securityModel = checked((int)header.ReadInteger());
			if (securityModel != 3)
				throw new InvalidDataException($"Expected USM security model 3, got {securityModel}.");
			return messageId;
		}

		internal readonly record struct SnmpEngineParameters(byte[] EngineId, int EngineBoots, int EngineTime);
	}
}
