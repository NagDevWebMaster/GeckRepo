namespace Unity.AI.Assistant.Skills
{
    /// <summary>
    /// Interface for skill resources that can provide their content on-demand.
    /// </summary>
    interface ISkillResource
    {
        /// <summary>
        /// The length of the text content.
        /// </summary>
        int Length { get; }

        /// <summary>
        /// Gets the content of the resource.
        /// </summary>
        /// <returns>The resource content as a string</returns>
        string GetContent();
    }
}
