using System;
using System.IO;
using System.Security.Cryptography;
using LocalRemoteDesktop.Models;

namespace LocalRemoteDesktop.Security
{
    internal static class SecureHandshake
    {
        private const string ServerChallengeRole = "server-challenge";
        private const string ClientProofRole = "client-proof";
        private const string ServerFinishedRole = "server-finished";

        internal static SecureSession AuthenticateClient(Stream stream, string accessCode)
        {
            var preSharedKey = SecurityPrimitives.DerivePreSharedKey(accessCode);
            var clientNonce = SecurityPrimitives.RandomBytes(SecurityPrimitives.NonceSize);
            byte[] serverNonce = null;
            byte[] expectedProof = null;
            byte[] clientProof = null;
            byte[] finishedProof = null;

            try
            {
                SendFrame(
                    stream,
                    FrameType.ClientHello,
                    AddVersion(clientNonce));

                var challenge = ReadExpectedFrame(
                    stream,
                    FrameType.ServerChallenge,
                    1 + SecurityPrimitives.NonceSize + SecurityPrimitives.ProofSize);
                EnsureVersion(challenge.Payload);

                serverNonce = CopyRange(
                    challenge.Payload,
                    1,
                    SecurityPrimitives.NonceSize);
                expectedProof = SecurityPrimitives.ComputeHandshakeProof(
                    preSharedKey,
                    ServerChallengeRole,
                    clientNonce,
                    serverNonce);
                if (!SecurityPrimitives.FixedTimeEquals(
                    expectedProof,
                    0,
                    challenge.Payload,
                    1 + SecurityPrimitives.NonceSize,
                    SecurityPrimitives.ProofSize))
                {
                    throw AuthenticationFailure();
                }

                clientProof = SecurityPrimitives.ComputeHandshakeProof(
                    preSharedKey,
                    ClientProofRole,
                    clientNonce,
                    serverNonce);
                SendFrame(
                    stream,
                    FrameType.ClientProof,
                    AddVersion(clientProof));

                var finished = ReadExpectedFrame(
                    stream,
                    FrameType.ServerProof,
                    1 + SecurityPrimitives.ProofSize);
                EnsureVersion(finished.Payload);

                finishedProof = SecurityPrimitives.ComputeHandshakeProof(
                    preSharedKey,
                    ServerFinishedRole,
                    clientNonce,
                    serverNonce);
                if (!SecurityPrimitives.FixedTimeEquals(
                    finishedProof,
                    0,
                    finished.Payload,
                    1,
                    SecurityPrimitives.ProofSize))
                {
                    throw AuthenticationFailure();
                }

                return SecureSession.CreateForClient(
                    preSharedKey,
                    clientNonce,
                    serverNonce);
            }
            finally
            {
                SecurityPrimitives.Clear(preSharedKey);
                SecurityPrimitives.Clear(clientNonce);
                SecurityPrimitives.Clear(serverNonce);
                SecurityPrimitives.Clear(expectedProof);
                SecurityPrimitives.Clear(clientProof);
                SecurityPrimitives.Clear(finishedProof);
            }
        }

        internal static SecureSession AuthenticateServer(
            Stream stream,
            byte[] preSharedKey)
        {
            byte[] clientNonce = null;
            byte[] serverNonce = null;
            byte[] challengeProof = null;
            byte[] expectedClientProof = null;
            byte[] finishedProof = null;

            try
            {
                var hello = ReadExpectedFrame(
                    stream,
                    FrameType.ClientHello,
                    1 + SecurityPrimitives.NonceSize);
                EnsureVersion(hello.Payload);
                clientNonce = CopyRange(
                    hello.Payload,
                    1,
                    SecurityPrimitives.NonceSize);

                serverNonce = SecurityPrimitives.RandomBytes(
                    SecurityPrimitives.NonceSize);
                challengeProof = SecurityPrimitives.ComputeHandshakeProof(
                    preSharedKey,
                    ServerChallengeRole,
                    clientNonce,
                    serverNonce);
                SendFrame(
                    stream,
                    FrameType.ServerChallenge,
                    CombineVersionNonceAndProof(serverNonce, challengeProof));

                var proof = ReadExpectedFrame(
                    stream,
                    FrameType.ClientProof,
                    1 + SecurityPrimitives.ProofSize);
                EnsureVersion(proof.Payload);
                expectedClientProof = SecurityPrimitives.ComputeHandshakeProof(
                    preSharedKey,
                    ClientProofRole,
                    clientNonce,
                    serverNonce);
                if (!SecurityPrimitives.FixedTimeEquals(
                    expectedClientProof,
                    0,
                    proof.Payload,
                    1,
                    SecurityPrimitives.ProofSize))
                {
                    throw AuthenticationFailure();
                }

                finishedProof = SecurityPrimitives.ComputeHandshakeProof(
                    preSharedKey,
                    ServerFinishedRole,
                    clientNonce,
                    serverNonce);
                SendFrame(
                    stream,
                    FrameType.ServerProof,
                    AddVersion(finishedProof));

                return SecureSession.CreateForServer(
                    preSharedKey,
                    clientNonce,
                    serverNonce);
            }
            finally
            {
                SecurityPrimitives.Clear(clientNonce);
                SecurityPrimitives.Clear(serverNonce);
                SecurityPrimitives.Clear(challengeProof);
                SecurityPrimitives.Clear(expectedClientProof);
                SecurityPrimitives.Clear(finishedProof);
            }
        }

        private static ProtocolFrame ReadExpectedFrame(
            Stream stream,
            FrameType expectedType,
            int expectedPayloadLength)
        {
            ProtocolFrame frame;
            try
            {
                frame = ProtocolFrame.ReadFrom(
                    stream,
                    ProtocolFrame.MaxHandshakePayloadSize);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is ObjectDisposedException ||
                ex is InvalidDataException)
            {
                throw AuthenticationFailure();
            }

            if (frame.Type != expectedType ||
                frame.Payload == null ||
                frame.Payload.Length != expectedPayloadLength)
            {
                throw AuthenticationFailure();
            }

            return frame;
        }

        private static void SendFrame(
            Stream stream,
            FrameType type,
            byte[] payload)
        {
            try
            {
                var data = new ProtocolFrame(type, payload).Serialize();
                stream.Write(data, 0, data.Length);
                stream.Flush();
            }
            finally
            {
                SecurityPrimitives.Clear(payload);
            }
        }

        private static byte[] AddVersion(byte[] value)
        {
            var result = new byte[1 + value.Length];
            result[0] = SecurityPrimitives.ProtocolVersion;
            Buffer.BlockCopy(value, 0, result, 1, value.Length);
            return result;
        }

        private static byte[] CombineVersionNonceAndProof(
            byte[] nonce,
            byte[] proof)
        {
            var result = new byte[1 + nonce.Length + proof.Length];
            result[0] = SecurityPrimitives.ProtocolVersion;
            Buffer.BlockCopy(nonce, 0, result, 1, nonce.Length);
            Buffer.BlockCopy(proof, 0, result, 1 + nonce.Length, proof.Length);
            return result;
        }

        private static byte[] CopyRange(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private static void EnsureVersion(byte[] payload)
        {
            if (payload[0] != SecurityPrimitives.ProtocolVersion)
                throw AuthenticationFailure();
        }

        private static CryptographicException AuthenticationFailure()
        {
            return new CryptographicException("远程端身份认证失败。");
        }
    }
}
