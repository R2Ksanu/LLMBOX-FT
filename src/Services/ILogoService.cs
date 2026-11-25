using System.Threading.Tasks;

namespace LlmodelBox.Services
{
    public interface ILogoService
    {
        string ExtractDomain(string urlOrDomain);
        Task<string> FetchAndCacheLogoAsync(string urlOrDomain);
        string GetCachedLogoPath(string domain);
    }
}
