using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using TraceLog;
using XDM.Core.Clients.Http;

namespace XDM.Core.MediaParser.YouTube
{
    /// <summary>
    /// Decrypts YouTube signature-cipher–protected stream URLs.
    ///
    /// YouTube sometimes delivers adaptive formats with a <c>signatureCipher</c> field
    /// instead of a plain <c>url</c>. The cipher contains the base URL, a scrambled
    /// signature (<c>s</c> parameter) and the query-parameter name that should carry
    /// the unscrambled signature (<c>sp</c>).
    ///
    /// To unscramble the signature we need the set of JavaScript transform functions
    /// that YouTube's player JS defines. This class downloads the player JS, extracts
    /// those functions, and replays them on the scrambled signature.
    /// </summary>
    public static class YouTubeCipherDecryptor
    {
        // Cache the transform actions so we don't re-download the player JS for
        // every format in the same manifest.
        private static readonly object _lock = new();
        private static string? _cachedPlayerUrl;
        private static List<CipherAction>? _cachedActions;

        /// <summary>
        /// Parses a <c>signatureCipher</c> query string and returns the decrypted
        /// download URL, or <c>null</c> if decryption fails.
        /// </summary>
        public static string? DecryptSignatureCipherUrl(string signatureCipher, string? playerJsUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(signatureCipher)) return null;

                // Parse the cipher into url, s, sp components
                var parts = ParseCipherParts(signatureCipher);
                if (parts.Url == null || parts.Signature == null) return null;

                var actions = GetCipherActions(playerJsUrl);
                if (actions == null || actions.Count == 0)
                {
                    Log.Debug("No cipher actions found – cannot decrypt");
                    return null;
                }

                var decrypted = ApplyActions(parts.Signature, actions);
                var sp = parts.SignatureParam ?? "sig";
                var separator = parts.Url.Contains('?') ? "&" : "?";
                return $"{parts.Url}{separator}{sp}={WebUtility.UrlEncode(decrypted)}";
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "YouTubeCipherDecryptor.DecryptSignatureCipherUrl failed");
                return null;
            }
        }

        /// <summary>
        /// Tries to extract the player JS URL from a YouTube <c>/youtubei/v1/player</c>
        /// JSON response body (the same manifest file the parser already downloaded).
        /// Falls back to a well-known player path pattern.
        /// </summary>
        public static string? ExtractPlayerJsUrl(string manifestPath)
        {
            try
            {
                var text = File.ReadAllText(manifestPath);

                // Look for "PLAYER_JS_URL":"..." or "jsUrl":"..." inside the JSON
                var patterns = new[]
                {
                    @"""jsUrl""\s*:\s*""([^""]+)""",
                    @"""PLAYER_JS_URL""\s*:\s*""([^""]+)""",
                    @"/s/player/[a-zA-Z0-9]+/[a-zA-Z0-9_]+\.js"
                };

                foreach (var pat in patterns)
                {
                    var m = Regex.Match(text, pat);
                    if (m.Success)
                    {
                        var raw = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                        if (!raw.StartsWith("http"))
                        {
                            raw = "https://www.youtube.com" + raw;
                        }
                        return raw;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ExtractPlayerJsUrl failed");
            }
            return null;
        }

        #region Cipher action extraction

        private static List<CipherAction>? GetCipherActions(string? playerJsUrl)
        {
            if (string.IsNullOrEmpty(playerJsUrl)) return null;

            lock (_lock)
            {
                if (_cachedPlayerUrl == playerJsUrl && _cachedActions != null)
                    return _cachedActions;

                var js = DownloadText(playerJsUrl!);
                if (js == null) return null;

                _cachedActions = ExtractCipherActions(js);
                _cachedPlayerUrl = playerJsUrl;
                return _cachedActions;
            }
        }

        /// <summary>
        /// Extracts the ordered list of cipher transform actions from the player JS.
        /// </summary>
        internal static List<CipherAction>? ExtractCipherActions(string js)
        {
            try
            {
                // Step 1 – find the main decipher function.
                // Pattern: function(a){a=a.split("");XX.YY(a,...);...;return a.join("")}
                // where XX is the helper object name.
                var decipherFuncMatch = Regex.Match(js,
                    @"\b([a-zA-Z0-9$]+)\s*=\s*function\s*\(\s*a\s*\)\s*\{\s*a\s*=\s*a\.split\(\s*""""\s*\)(.+?)return\s+a\.join\(\s*""""\s*\)");

                if (!decipherFuncMatch.Success)
                {
                    // Alternative pattern – named function declaration
                    decipherFuncMatch = Regex.Match(js,
                        @"function\s+([a-zA-Z0-9$]+)\s*\(\s*a\s*\)\s*\{\s*a\s*=\s*a\.split\(\s*""""\s*\)(.+?)return\s+a\.join\(\s*""""\s*\)");
                }

                if (!decipherFuncMatch.Success)
                {
                    Log.Debug("Could not find decipher function in player JS");
                    return null;
                }

                var body = decipherFuncMatch.Groups[2].Value;

                // Step 2 – identify helper object name  e.g. "Xy" from "Xy.ab(a,3)"
                var helperMatch = Regex.Match(body, @"([a-zA-Z0-9$]+)\.[a-zA-Z0-9$]+\(");
                if (!helperMatch.Success) return null;
                var helperObj = helperMatch.Groups[1].Value;

                // Step 3 – find the helper object definition
                var escapedName = Regex.Escape(helperObj);
                var objMatch = Regex.Match(js,
                    $@"var\s+{escapedName}\s*=\s*\{{([\s\S]*?)\}}\s*;");
                if (!objMatch.Success) return null;
                var objBody = objMatch.Groups[1].Value;

                // Step 4 – classify each method in the helper object
                var methodMap = new Dictionary<string, CipherActionType>();
                var methodMatches = Regex.Matches(objBody,
                    @"([a-zA-Z0-9$]+)\s*:\s*function\s*\([^)]*\)\s*\{([^}]+)\}");
                foreach (Match mm in methodMatches)
                {
                    var name = mm.Groups[1].Value;
                    var mBody = mm.Groups[2].Value;
                    if (mBody.Contains("reverse"))
                        methodMap[name] = CipherActionType.Reverse;
                    else if (mBody.Contains("splice"))
                        methodMap[name] = CipherActionType.Splice;
                    else // swap
                        methodMap[name] = CipherActionType.Swap;
                }

                // Step 5 – parse the call sequence in the decipher function body
                var actions = new List<CipherAction>();
                var callMatches = Regex.Matches(body,
                    $@"{escapedName}\.([a-zA-Z0-9$]+)\(\s*a\s*,\s*(\d+)\s*\)");
                foreach (Match cm in callMatches)
                {
                    var mName = cm.Groups[1].Value;
                    var arg = int.Parse(cm.Groups[2].Value);
                    if (methodMap.TryGetValue(mName, out var actionType))
                    {
                        actions.Add(new CipherAction { Type = actionType, Argument = arg });
                    }
                }

                Log.Debug($"Extracted {actions.Count} cipher actions from player JS");
                return actions;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "ExtractCipherActions failed");
                return null;
            }
        }

        #endregion

        #region Apply transforms

        private static string ApplyActions(string signature, List<CipherAction> actions)
        {
            var a = signature.ToCharArray();
            foreach (var action in actions)
            {
                switch (action.Type)
                {
                    case CipherActionType.Reverse:
                        Array.Reverse(a);
                        break;
                    case CipherActionType.Splice:
                        a = a.Skip(action.Argument).ToArray();
                        break;
                    case CipherActionType.Swap:
                        var tmp = a[0];
                        a[0] = a[action.Argument % a.Length];
                        a[action.Argument % a.Length] = tmp;
                        break;
                }
            }
            return new string(a);
        }

        #endregion

        #region Parse cipher query string

        private static CipherParts ParseCipherParts(string cipher)
        {
            var parts = new CipherParts();
            foreach (var segment in cipher.Split('&'))
            {
                var idx = segment.IndexOf('=');
                if (idx < 0) continue;
                var key = segment.Substring(0, idx);
                var val = WebUtility.UrlDecode(segment.Substring(idx + 1));

                switch (key)
                {
                    case "url":
                        parts.Url = val;
                        break;
                    case "s":
                        parts.Signature = val;
                        break;
                    case "sp":
                        parts.SignatureParam = val;
                        break;
                }
            }
            return parts;
        }

        private struct CipherParts
        {
            public string? Url;
            public string? Signature;
            public string? SignatureParam;
        }

        #endregion

        #region Helpers

        private static string? DownloadText(string url)
        {
            try
            {
                using var client = HttpClientFactory.NewHttpClient(null);
                client.Timeout = TimeSpan.FromSeconds(15);
                var headers = new Dictionary<string, List<string>>
                {
                    ["Accept"] = new List<string> { "*/*" },
                    ["User-Agent"] = new List<string>
                    {
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                    }
                };
                var request = client.CreateGetRequest(new Uri(url), headers, null, null);
                using var response = client.Send(request);
                using var sr = new StreamReader(response.GetResponseStream(), Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "DownloadText failed for: " + url);
                return null;
            }
        }

        #endregion
    }

    internal enum CipherActionType
    {
        Reverse,
        Splice,
        Swap
    }

    internal class CipherAction
    {
        public CipherActionType Type { get; set; }
        public int Argument { get; set; }
    }
}
