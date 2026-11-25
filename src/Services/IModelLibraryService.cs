using System.Collections.Generic;
using LlmodelBox.Models;

namespace LlmodelBox.Services
{
    public interface IModelLibraryService
    {
        List<ModelItem> LoadModels();
        bool SaveModels(List<ModelItem> models);
        void RegisterCustomProviderModel(CustomProviderItem provider, List<ModelItem> allModels, List<ModelItem> filteredModels);
        List<ModelItem> FilterModels(List<ModelItem> allModels, string filterPill, string searchQuery);
        List<ModelItem> GetDefaultModels();
        List<CompanyCategoryItem> GetCompanyCategories(List<ModelItem> allModels, UserSettings settings, ICloudApiService cloudApiService);
        List<CompanyCategoryItem> FilterCompanyCategories(List<CompanyCategoryItem> categories, string filterPill, string searchQuery);
    }
}
