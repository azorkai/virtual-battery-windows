using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VirtualBattery
{
    /// <summary>
    /// Signs the driver catalog with a key that exists only for the duration of the call.
    /// The key is generated on this PC, cannot be exported, and is deleted right after
    /// signing, so the certificate left behind can never sign anything else.
    /// </summary>
    static class CatalogSigner
    {
        public const string Subject = "CN=Virtual Battery (this PC only)";

        /// <summary>Signs the file and returns the public certificate.</summary>
        public static X509Certificate2 Sign(string path)
        {
            var parameters = new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None));

            CngKey key = CngKey.Create(CngAlgorithm.Rsa, "VirtualBattery-" + Guid.NewGuid().ToString("N"), parameters);
            try
            {
                using (var rsa = new RSACng(key))
                using (X509Certificate2 certificate = CreateCertificate(rsa))
                {
                    SignFile(path, certificate);
                    return new X509Certificate2(certificate.Export(X509ContentType.Cert));
                }
            }
            finally
            {
                key.Delete();
            }
        }

        static X509Certificate2 CreateCertificate(RSA rsa)
        {
            var request = new CertificateRequest(Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, true)); // code signing only
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        }

        static void SignFile(string path, X509Certificate2 certificate)
        {
            var extended = new SignExtendedInfo
            {
                Size = Marshal.SizeOf(typeof(SignExtendedInfo)),
                HashAlgorithm = "2.16.840.1.101.3.4.2.1", // SHA-256
            };
            IntPtr extendedPtr = Marshal.AllocHGlobal(extended.Size);
            Marshal.StructureToPtr(extended, extendedPtr, false);
            try
            {
                var info = new SignInfo
                {
                    Size = Marshal.SizeOf(typeof(SignInfo)),
                    SubjectChoice = 1,      // file
                    FileName = path,
                    CertChoice = 1,         // certificate context
                    Certificate = certificate.Handle,
                    ExtendedInfo = extendedPtr,
                };
                if (!CryptUIWizDigitalSign(1 /* no UI */, IntPtr.Zero, null, ref info, IntPtr.Zero))
                    throw new Win32Exception();
            }
            finally
            {
                Marshal.DestroyStructure(extendedPtr, typeof(SignExtendedInfo));
                Marshal.FreeHGlobal(extendedPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SignInfo
        {
            public int Size;
            public int SubjectChoice;
            [MarshalAs(UnmanagedType.LPWStr)] public string FileName;
            public int CertChoice;
            public IntPtr Certificate;
            [MarshalAs(UnmanagedType.LPWStr)] public string TimestampUrl;
            public int AdditionalCertChoice;
            public IntPtr ExtendedInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SignExtendedInfo
        {
            public int Size;
            public int AttrFlags;
            [MarshalAs(UnmanagedType.LPWStr)] public string Description;
            [MarshalAs(UnmanagedType.LPWStr)] public string MoreInfo;
            [MarshalAs(UnmanagedType.LPStr)] public string HashAlgorithm;
            [MarshalAs(UnmanagedType.LPWStr)] public string DisplayString;
            public IntPtr AdditionalStore;
            public IntPtr Authenticated;
            public IntPtr Unauthenticated;
        }

        [DllImport("cryptui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool CryptUIWizDigitalSign(int flags, IntPtr parent, string title, ref SignInfo info, IntPtr context);
    }
}
