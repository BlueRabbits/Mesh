﻿/*
Technitium Mesh
Copyright (C) 2018  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System.Collections.Generic;
using System.IO;
using System.Net;
using TechnitiumLibrary.IO;
using TechnitiumLibrary.Security.Cryptography;

namespace MeshCore.Network.SecureChannel
{
    public class SecureChannelClientStream : SecureChannelStream
    {
        #region variables

        readonly SecureChannelCipherSuite _supportedCiphers;
        readonly SecureChannelOptions _options;
        readonly byte[] _preSharedKey;
        readonly BinaryNumber _meshId;
        readonly byte[] _privateKey;
        IEnumerable<BinaryNumber> _trustedMeshIds;

        #endregion

        #region constructor

        public SecureChannelClientStream(Stream stream, IPEndPoint remotePeerEP, int renegotiateAfterBytesSent, int renegotiateAfterSeconds, SecureChannelCipherSuite supportedCiphers, SecureChannelOptions options, byte[] preSharedKey, BinaryNumber meshId, byte[] privateKey, IEnumerable<BinaryNumber> trustedMeshIds)
            : base(remotePeerEP, renegotiateAfterBytesSent, renegotiateAfterSeconds)
        {
            _supportedCiphers = supportedCiphers;
            _options = options;
            _preSharedKey = preSharedKey;
            _meshId = meshId;
            _privateKey = privateKey;
            _trustedMeshIds = trustedMeshIds;

            Start(stream);
        }

        #endregion

        #region private

        private void Start(Stream stream)
        {
            try
            {
                WriteBufferedStream bufferedStream = new WriteBufferedStream(stream, 8 * 1024);

                //write client hello
                SecureChannelHandshakeHello clientHello = new SecureChannelHandshakeHello(_supportedCiphers, _options);
                clientHello.WriteTo(bufferedStream);
                bufferedStream.Flush();

                //read server hello
                SecureChannelHandshakeHello serverHello = new SecureChannelHandshakeHello(bufferedStream);

                switch (serverHello.Version)
                {
                    case 1:
                        ProtocolV1(bufferedStream, serverHello, clientHello);
                        break;

                    default:
                        throw new SecureChannelException(SecureChannelCode.ProtocolVersionNotSupported, _remotePeerEP, _remotePeerMeshId, "SecureChannel protocol version not supported: " + serverHello.Version);
                }
            }
            catch (SecureChannelException ex)
            {
                if (ex.Code == SecureChannelCode.RemoteError)
                {
                    throw new SecureChannelException(ex.Code, _remotePeerEP, _remotePeerMeshId, ex.Message, ex);
                }
                else
                {
                    try
                    {
                        Stream s;

                        if (_baseStream == null)
                            s = stream;
                        else
                            s = this;

                        new SecureChannelHandshakePacket(ex.Code).WriteTo(s);
                        s.Flush();
                    }
                    catch
                    { }

                    if (ex.PeerEP == null)
                        throw new SecureChannelException(ex.Code, _remotePeerEP, _remotePeerMeshId, ex.Message, ex);

                    throw;
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                try
                {
                    Stream s;

                    if (_baseStream == null)
                        s = stream;
                    else
                        s = this;

                    new SecureChannelHandshakePacket(SecureChannelCode.UnknownException).WriteTo(s);
                    s.Flush();
                }
                catch
                { }

                throw;
            }
        }

        private void ProtocolV1(WriteBufferedStream bufferedStream, SecureChannelHandshakeHello serverHello, SecureChannelHandshakeHello clientHello)
        {
            #region 1. hello handshake check

            //read selected crypto option
            _selectedCipher = _supportedCiphers & serverHello.SupportedCiphers;

            if (_selectedCipher == SecureChannelCipherSuite.None)
                throw new SecureChannelException(SecureChannelCode.NoMatchingCipherAvailable, _remotePeerEP, _remotePeerMeshId);

            //match options
            if (_options != serverHello.Options)
                throw new SecureChannelException(SecureChannelCode.NoMatchingOptionsAvailable, _remotePeerEP, _remotePeerMeshId);

            #endregion

            #region 2. key exchange

            //read server key exchange data
            SecureChannelHandshakeKeyExchange serverKeyExchange = new SecureChannelHandshakeKeyExchange(bufferedStream);

            if (_options.HasFlag(SecureChannelOptions.PRE_SHARED_KEY_AUTHENTICATION_REQUIRED))
            {
                if (!serverKeyExchange.IsPskAuthValid(serverHello, clientHello, _preSharedKey))
                    throw new SecureChannelException(SecureChannelCode.PskAuthenticationFailed, _remotePeerEP, _remotePeerMeshId);
            }

            KeyAgreement keyAgreement;

            switch (_selectedCipher)
            {
                case SecureChannelCipherSuite.DHE2048_ANON_WITH_AES256_CBC_HMAC_SHA256:
                case SecureChannelCipherSuite.DHE2048_RSA2048_WITH_AES256_CBC_HMAC_SHA256:
                    keyAgreement = new DiffieHellman(DiffieHellmanGroupType.RFC3526, 2048, KeyAgreementKeyDerivationFunction.Hmac, KeyAgreementKeyDerivationHashAlgorithm.SHA256);
                    break;

                default:
                    throw new SecureChannelException(SecureChannelCode.NoMatchingCipherAvailable, _remotePeerEP, _remotePeerMeshId);
            }

            //write client key exchange data
            SecureChannelHandshakeKeyExchange clientKeyExchange = new SecureChannelHandshakeKeyExchange(keyAgreement, serverHello, clientHello, _preSharedKey);
            clientKeyExchange.WriteTo(bufferedStream);
            bufferedStream.Flush();

            #endregion

            #region 3. enable encryption

            EnableEncryption(bufferedStream, serverHello, clientHello, keyAgreement, serverKeyExchange);

            #endregion

            #region 4. MeshId based authentication

            switch (_selectedCipher)
            {
                case SecureChannelCipherSuite.DHE2048_RSA2048_WITH_AES256_CBC_HMAC_SHA256:
                    if (_options.HasFlag(SecureChannelOptions.CLIENT_AUTHENTICATION_REQUIRED))
                    {
                        //write client auth
                        new SecureChannelHandshakeAuthentication(clientKeyExchange, serverHello, clientHello, _meshId, _privateKey).WriteTo(this);
                        this.Flush();
                    }

                    //read server auth
                    SecureChannelHandshakeAuthentication serverAuth = new SecureChannelHandshakeAuthentication(this);
                    _remotePeerMeshId = serverAuth.MeshId;

                    //authenticate server
                    if (!serverAuth.IsSignatureValid(serverKeyExchange, serverHello, clientHello))
                        throw new SecureChannelException(SecureChannelCode.PeerAuthenticationFailed, _remotePeerEP, _remotePeerMeshId);

                    //check if server is trusted
                    if (!serverAuth.IsTrustedMeshId(_trustedMeshIds))
                        throw new SecureChannelException(SecureChannelCode.UntrustedRemotePeerMeshId, _remotePeerEP, _remotePeerMeshId);

                    break;

                case SecureChannelCipherSuite.DHE2048_ANON_WITH_AES256_CBC_HMAC_SHA256:
                    break; //no auth for ANON

                default:
                    throw new SecureChannelException(SecureChannelCode.NoMatchingCipherAvailable, _remotePeerEP, _remotePeerMeshId);
            }

            #endregion
        }

        #endregion

        #region overrides

        protected override void StartRenegotiation()
        {
            Start(_baseStream);
        }

        #endregion
    }
}