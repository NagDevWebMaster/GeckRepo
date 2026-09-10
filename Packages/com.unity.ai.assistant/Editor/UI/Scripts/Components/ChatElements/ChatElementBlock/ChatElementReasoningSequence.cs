using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.Assistant.FunctionCalling;
using Unity.AI.Assistant.UI.Editor.Scripts.Data.MessageBlocks;
using UnityEngine.UIElements;

namespace Unity.AI.Assistant.UI.Editor.Scripts.Components.ChatElements
{
    /// <summary>
    /// Groups consecutive reasoning content (thoughts and function calls) into a collapsible foldout.
    /// Shows all content when expanded, with the foldout title showing the last item's type and title.
    /// </summary>
    class ChatElementReasoningSequence : ManagedTemplate
    {
        readonly List<ChatElementBlockThought> m_ThoughtBlocks = new();
        readonly List<ChatElementBlockFunctionCall> m_FunctionCallBlocks = new();
        readonly List<ChatElementBlockAcpToolCall> m_AcpToolCallBlocks = new();
        readonly Dictionary<string, SubagentHeaderElement> m_AgentHeaders = new();

        const string k_SpawnSubagentToolId = "Agent.SpawnSubagent";
        const string k_RoleParam = "role";
        readonly List<FunctionCallBlockModel> m_PendingSpawnCalls = new();

        Foldout m_Foldout;
        VisualElement m_ContentContainer;

        string m_LastTitle;
        bool m_UserInteracted;

        public int ThoughtCount => m_ThoughtBlocks.Count;

        public ChatElementReasoningSequence() : base(AssistantUIConstants.UIModulePath)
        {
            SetResourceName("ChatElementReasoningSequence");
        }

        protected override void InitializeView(TemplateContainer view)
        {
            m_Foldout = view.Q<Foldout>("reasoningFoldout");
            m_ContentContainer = view.Q("reasoningContent");
            m_Foldout.SetValueWithoutNotify(true); // Start expanded
            
            m_Foldout.RegisterValueChangedCallback(OnFoldoutChanged);
            UpdateFoldoutTitle(); // Set initial state (expanded = no title)
        }
        
        void OnFoldoutChanged(ChangeEvent<bool> evt)
        {
            m_UserInteracted = true;
            UpdateFoldoutTitle();
        }

        public void ResetInteraction()
        {
            m_UserInteracted = false;
        }

        /// <summary>
        /// Adds a thought to the sequence.
        /// </summary>
        public void AddThought(ThoughtBlockModel model)
        {
            var thoughtBlock = new ChatElementBlockThought();
            thoughtBlock.Initialize(Context);
            thoughtBlock.SetBlockModel(model);

            m_ThoughtBlocks.Add(thoughtBlock);
            m_ContentContainer.Add(thoughtBlock);

            // Update foldout title with this thought's title
            var title = ChatElementBlockThought.ExtractTitle(model.Content, out _);
            m_LastTitle = title;
            UpdateFoldoutTitle();
        }

        /// <summary>
        /// Updates an existing thought at the given index.
        /// </summary>
        public void UpdateThought(int index, ThoughtBlockModel model)
        {
            if (index >= 0 && index < m_ThoughtBlocks.Count)
            {
                m_ThoughtBlocks[index].SetBlockModel(model);

                // Always update the last title from the most recent thought
                // This ensures title is available when foldout is collapsed
                var title = ChatElementBlockThought.ExtractTitle(model.Content, out _);
                if (!string.IsNullOrEmpty(title))
                {
                    m_LastTitle = title;
                    UpdateFoldoutTitle();
                }
            }
        }

        /// <summary>
        /// Adds a function call block to the sequence (full interactive element).
        /// </summary>
        public ChatElementBlockFunctionCall AddFunctionCall(FunctionCallBlockModel model)
        {
            var block = new ChatElementBlockFunctionCall();
            block.Initialize(Context);
            block.SetBlockModel(model);

            m_FunctionCallBlocks.Add(block);

            if (model.Call.FunctionId == k_SpawnSubagentToolId)
            {
                var role = model.Call.Parameters?[k_RoleParam]?.ToString();
                if (!string.IsNullOrEmpty(role))
                {
                    var matchKey = $"{SubagentHeaderElement.SubagentPrefix}{role}";
                    var matched = false;
                    foreach (var kvp in m_AgentHeaders)
                    {
                        if (!kvp.Value.HasSpawnCall
                            && kvp.Key.StartsWith(matchKey, StringComparison.OrdinalIgnoreCase))
                        {
                            kvp.Value.SetSpawnCallModel(model);
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        m_PendingSpawnCalls.Add(model);
                    }
                }
            }

            var agent = model.Call.Agent;
            if (SubagentHeaderElement.IsSubagent(agent))
            {
                if (!m_AgentHeaders.TryGetValue(agent, out var agentHeader))
                {
                    agentHeader = new SubagentHeaderElement();
                    agentHeader.Initialize(Context);
                    agentHeader.SetAgent(agent);
                    m_AgentHeaders[agent] = agentHeader;
                    m_ContentContainer.Add(agentHeader);

                    for (int i = 0; i < m_PendingSpawnCalls.Count; i++)
                    {
                        var spawnModel = m_PendingSpawnCalls[i];
                        var role = spawnModel.Call.Parameters?[k_RoleParam]?.ToString();
                        if (!string.IsNullOrEmpty(role))
                        {
                            var matchKey = $"{SubagentHeaderElement.SubagentPrefix}{role}";
                            if (agent.StartsWith(matchKey, StringComparison.OrdinalIgnoreCase))
                            {
                                agentHeader.SetSpawnCallModel(spawnModel);
                                m_PendingSpawnCalls.RemoveAt(i);
                                break; // required — forward iteration with RemoveAt is only safe with immediate break
                            }
                        }
                    }
                }

                agentHeader.AddContent(block);
            }
            else
            {
                m_ContentContainer.Add(block);
            }

            // Update foldout title - use same display name as default agent tool renderer
            m_LastTitle = model.Call.GetDefaultTitle();
            UpdateFoldoutTitle();

            // Update progress for the subagent that owns this block
            if (agent != null && m_AgentHeaders.ContainsKey(agent))
            {
                UpdateAgentProgress(agent);
            }

            return block;
        }

        void UpdateAgentProgress(string agent)
        {
            if (!m_AgentHeaders.TryGetValue(agent, out var header))
                return;

            int total = 0;
            int completed = 0;
            foreach (var block in m_FunctionCallBlocks)
            {
                if (block.Agent != agent)
                    continue;
                total++;
                if (block.IsDone)
                    completed++;
            }

            header.UpdateProgress(completed, total);
        }

        public void RefreshSpawnCallModels(Dictionary<Guid, FunctionCallBlockModel> blocksByCallId)
        {
            foreach (var kvp in m_AgentHeaders)
            {
                var callId = kvp.Value.SpawnCallId;
                if (!callId.HasValue) continue;
                if (blocksByCallId.TryGetValue(callId.Value, out var model))
                {
                    kvp.Value.SetSpawnCallModel(model);
                }
            }
        }

        public void UpdateAllAgentProgress()
        {
            foreach (var agent in m_AgentHeaders.Keys)
            {
                UpdateAgentProgress(agent);
            }
        }

        /// <summary>
        /// Gets an existing function call block by ID.
        /// </summary>
        public ChatElementBlockFunctionCall GetFunctionCall(string callId)
        {
            return m_FunctionCallBlocks.FirstOrDefault(b => b.CallId.ToString() == callId);
        }

        /// <summary>
        /// Removes a function call block by call ID from this sequence.
        /// </summary>
        public bool RemoveFunctionCall(string callId)
        {
            var block = m_FunctionCallBlocks.FirstOrDefault(b => b.CallId.ToString() == callId);
            if (block == null) return false;
            m_FunctionCallBlocks.Remove(block);
            block.RemoveFromHierarchy();
            return true;
        }

        /// <summary>
        /// Adds an ACP tool call block to the sequence.
        /// </summary>
        public ChatElementBlockAcpToolCall AddAcpToolCall(AcpToolCallBlockModel model)
        {
            var block = new ChatElementBlockAcpToolCall();
            block.Initialize(Context);
            block.SetBlockModel(model);

            m_AcpToolCallBlocks.Add(block);
            m_ContentContainer.Add(block);

            m_LastTitle = AcpToolCallElement.GetDisplayTitle(model.CallInfo?.Title, model.CallInfo?.ToolName);
            UpdateFoldoutTitle();

            return block;
        }

        /// <summary>
        /// Gets an existing ACP tool call block by tool call ID.
        /// </summary>
        public ChatElementBlockAcpToolCall GetAcpToolCall(string toolCallId)
        {
            return m_AcpToolCallBlocks.FirstOrDefault(b => b.ToolCallId == toolCallId);
        }

        void UpdateFoldoutTitle()
        {
            // When expanded, show just "Thoughts"; when collapsed, show full title
            if (m_Foldout.value)
            {
                m_Foldout.text = "Thoughts";
            }
            else
            {
                if (!string.IsNullOrEmpty(m_LastTitle))
                    m_Foldout.text = $"Thoughts - {m_LastTitle}";
                else
                    m_Foldout.text = "Thoughts";
            }
        }

        public bool HasPendingFunctionCalls()
        {
            return m_FunctionCallBlocks.Any(b => !b.IsDone)
                || m_AcpToolCallBlocks.Any(b => !b.IsDone);
        }

        public void Collapse()
        {
            if (m_UserInteracted)
                return;

            m_Foldout.SetValueWithoutNotify(false);
            UpdateFoldoutTitle();
        }

        public void OnConversationCancelled()
        {
            foreach (var block in m_ThoughtBlocks)
            {
                block.OnConversationCancelled();
            }

            foreach (var block in m_FunctionCallBlocks)
            {
                block.OnConversationCancelled();
            }

            foreach (var block in m_AcpToolCallBlocks)
            {
                block.OnConversationCancelled();
            }

            foreach (var header in m_AgentHeaders.Values)
            {
                header.TryMarkFailed();
            }

            m_PendingSpawnCalls.Clear();
        }
    }
}
