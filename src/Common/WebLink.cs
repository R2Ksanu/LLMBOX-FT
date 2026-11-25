using System;

namespace LlmodelBox.Common
{
    public static class WebLink
    {
        public static bool TryParse(string? value, out Uri? uri)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)) return true;
            uri = null;
            return false;
        }
    }
}
