using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace Lunaris
{
    internal sealed class ReleaseInfo
    {
        internal readonly string Version;
        internal readonly string Url;

        internal ReleaseInfo(string version, string url)
        {
            Version = version;
            Url = url;
        }
    }

    internal sealed class ReleaseUpdates : IDisposable
    {
        internal const string ProjectUrl = "https://github.com/Dosash/lunaris-dock";
        private const string LatestUrl = "https://api.github.com/repos/Dosash/lunaris-dock/releases/latest";
        private static readonly Version Installed = typeof(ReleaseUpdates).Assembly.GetName().Version;
        internal static readonly string CurrentVersion = DisplayVersion(Installed);
        private readonly HttpClient client;

        [DataContract]
        private sealed class Release
        {
            [DataMember(Name="tag_name", IsRequired=true)] public string Tag { get; set; }
            [DataMember(Name="draft", IsRequired=true)] public bool Draft { get; set; }
            [DataMember(Name="prerelease", IsRequired=true)] public bool Prerelease { get; set; }
        }

        internal ReleaseUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

        internal ReleaseUpdates(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) {
                Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LunarisDock/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        private static string DisplayVersion(Version version)
        {
            return version.ToString(version.Revision > 0 ? 4 : 3);
        }

        private static Version WithRevision(Version version)
        {
            return version.Revision >= 0 ? version : new Version(version.Major, version.Minor, Math.Max(0, version.Build), 0);
        }

        internal async Task<ReleaseInfo> LatestAsync()
        {
            using (var response = await client.GetAsync(LatestUrl).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    return ReadRelease(stream, Installed);
            }
        }

        internal static ReleaseInfo ReadRelease(Stream stream, Version installed)
        {
            var serializer = new DataContractJsonSerializer(typeof(Release));
            var release = (Release)serializer.ReadObject(stream);
            if (release == null || release.Draft || release.Prerelease || String.IsNullOrEmpty(release.Tag)) return null;
            string tag = release.Tag;
            string number = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
            Version parsed;
            if (!Version.TryParse(number, out parsed) || parsed.Build < 0 || parsed.ToString() != number) return null;
            var version = WithRevision(parsed);
            if (version <= WithRevision(installed)) return null;
            return new ReleaseInfo(DisplayVersion(version), ProjectUrl + "/releases/tag/" + tag);
        }

        public void Dispose() { client.Dispose(); }
    }
}
