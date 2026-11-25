namespace LlmodelBox.Models
{
    public class ProjectIdea
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Stack { get; set; } = string.Empty;

        public string StackBadge => string.IsNullOrWhiteSpace(Stack) ? "General" : Stack;
    }
}
