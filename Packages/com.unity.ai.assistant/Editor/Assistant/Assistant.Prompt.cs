using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unity.AI.Assistant.Agents;
using Unity.AI.Assistant.Backend;
using Unity.AI.Assistant.Bridge.Editor;
using Unity.AI.Assistant.Data;
using Unity.AI.Assistant.Editor.Analytics;
using Unity.AI.Assistant.Editor.Backend.Socket;
using Unity.AI.Assistant.Editor.Context;
using Unity.AI.Assistant.Editor.RunCommand;
using Unity.AI.Assistant.Editor.Utils;
using UnityEditor;
using UnityEngine;
using Unity.AI.Assistant.Socket.ErrorHandling;
using Unity.AI.Assistant.Socket.Protocol.Models.FromClient;
using Unity.AI.Assistant.Socket.Workflows.Chat;
using Unity.AI.Assistant.Utils;
using Unity.AI.Assistant.Editor.Checkpoint;
using OrchestrationDataUtilities = Unity.AI.Assistant.Socket.Utilities.OrchestrationDataUtilities;
using TaskUtils = Unity.AI.Assistant.Editor.Utils.TaskUtils;

namespace Unity.AI.Assistant.Editor
{
    delegate void ChangePromptStateDelegate(AssistantConversationId conversationId, Assistant.PromptState newState, string message, bool force = false);
    
    /// <summary>
    /// Encapsulates workflow event handling logic for Assistant conversations.
    /// Handles chat responses, function calls, and workflow state changes.
    /// </summary>
    class WorkflowEventHandler
    {
        readonly IChatWorkflow m_Workflow;
        readonly AssistantConversation m_Conversation;
        readonly AssistantMessage m_AssistantMessage;
        readonly StringBuilder m_ResponseBuilder;
        readonly CancellationToken m_CancellationToken;
        readonly bool m_IsNewConversation;
        readonly ChangePromptStateDelegate m_ChangePromptState;
        readonly Action<AssistantConversationId, ErrorInfo> m_ConversationErrorOccured;
        readonly Action<AssistantConversationId> m_CapacityReached;
        readonly Action<AssistantConversation> m_NotifyConversationChange;
        readonly Action<AssistantConversationId> m_IncompleteMessageCompleted;

        long m_PromptSentAt;
        bool m_FirstChunkSeen;
        int m_TurnEndReported;

        /// <summary>
        /// Stamp the time at which the user prompt was sent to the backend. Used to compute
        /// client-side Time To First Chunk (TTFT) when the first response fragment arrives.
        /// Must be called synchronously before awaiting <c>SendChatRequest</c>. Not called on
        /// the resume path, in which case TTFT reporting is skipped (sentinel-out at 0).
        /// </summary>
        public void SetPromptSentAt(long unixMs)
        {
            m_PromptSentAt = unixMs;
        }

        void ReportTurnEnded(string outcome, string failureReason)
        {
            if (Interlocked.Exchange(ref m_TurnEndReported, 1) == 1)
                return;

            // EditorAnalytics must run on the main thread; HandleChatResponse/HandleClose do not.
            var conversationId = m_Conversation.Id.IsValid ? m_Conversation.Id.Value : null;
            MainThread.DispatchAndForget(() =>
                AIAssistantAnalytics.ReportGatewayTurnEndedEvent(conversationId, outcome, failureReason));
        }

        public WorkflowEventHandler(
            IChatWorkflow workflow,
            AssistantConversation conversation,
            AssistantMessage assistantMessage,
            StringBuilder responseBuilder,
            CancellationToken cancellationToken,
            bool isNewConversation,
            ChangePromptStateDelegate changePromptState,
            Action<AssistantConversationId, ErrorInfo> conversationErrorOccured,
            Action<AssistantConversationId> capacityReached,
            Action<AssistantConversation> notifyConversationChange,
            Action<AssistantConversationId> incompleteMessageCompleted = null)
        {
            m_Workflow = workflow;
            m_Conversation = conversation;
            m_AssistantMessage = assistantMessage;
            m_ResponseBuilder = responseBuilder;
            m_CancellationToken = cancellationToken;
            m_IsNewConversation = isNewConversation;
            m_ChangePromptState = changePromptState;
            m_ConversationErrorOccured = conversationErrorOccured;
            m_CapacityReached = capacityReached;
            m_NotifyConversationChange = notifyConversationChange;
            m_IncompleteMessageCompleted = incompleteMessageCompleted;
        }

        public void Subscribe()
        {
            m_Workflow.OnChatResponse -= HandleChatResponse;
            m_Workflow.OnChatResponse += HandleChatResponse;

            m_Workflow.OnClose -= HandleClose;
            m_Workflow.OnClose += HandleClose;
            m_Workflow.OnWorkflowStateChanged -= OnWorkflowStateChange;
            m_Workflow.OnWorkflowStateChanged += OnWorkflowStateChange;
        }

        public void Unsubscribe()
        {
            m_Workflow.OnClose -= HandleClose;
            m_Workflow.OnChatResponse -= HandleChatResponse;
            m_Workflow.OnWorkflowStateChanged -= OnWorkflowStateChange;
        }

        void HandleClose(CloseReason reason)
        {
            if (reason.Reason == CloseReason.ReasonType.ServerDisconnectedGracefully
                || reason.Reason == CloseReason.ReasonType.ClientCanceled
                || m_CancellationToken.IsCancellationRequested)
            {
                if (reason.Reason == CloseReason.ReasonType.ClientCanceled
                    || m_CancellationToken.IsCancellationRequested)
                    ReportTurnEnded("cancelled", null);
                else
                    ReportTurnEnded("session_ended", null);
                return;
            }

            if (reason.Reason == CloseReason.ReasonType.ServerNoCapacity)
            {
                // The in-flight assistant message never received content (the server disconnected for
                // capacity). Remove the empty placeholder so the UI doesn't render a blank response
                // bubble; the UI layer orchestrates the capacity fallback (and resend) instead.
                m_Conversation.Messages.Remove(m_AssistantMessage);
                m_NotifyConversationChange?.Invoke(m_Conversation);
                Unsubscribe();
                // The placeholder is removed rather than completed normally, so release the
                // incomplete-message tracking — otherwise domain-reload recovery would try to resume a
                // message that no longer exists and the UI's "incomplete" state would stay set.
                m_IncompleteMessageCompleted?.Invoke(m_Conversation.Id);
                MainThread.DispatchAndForget(() => m_CapacityReached?.Invoke(m_Conversation.Id));
                ReportTurnEnded("error", "no_capacity");
                return;
            }

            bool isInformational = reason.Reason == CloseReason.ReasonType.ServerDisconnectedInformational;
            bool isTransportOrNetwork =
                reason.Reason == CloseReason.ReasonType.CouldNotConnect
                || reason.Reason == CloseReason.ReasonType.UnderlyingWebSocketWasClosed
                || reason.Reason == CloseReason.ReasonType.ChatResponseTimeout
                || reason.Reason == CloseReason.ReasonType.DiscussionInitializationTimeout;

            string message = isTransportOrNetwork
                ? $"The connection to the AI Assistant was lost. {ErrorHandlingUtility.ErrorMessageNetworkedSuffix}"
                : !string.IsNullOrEmpty(reason.Info)
                    ? reason.Info
                    : $"Something went wrong. {ErrorHandlingUtility.ErrorMessageNetworkedSuffix}";

            var messageId = new AssistantMessageId(
                m_Conversation.Id,
                Guid.NewGuid().ToString(),
                AssistantMessageIdType.Internal);

            var closeMessage = isInformational
                ? AssistantMessage.AsInformational(messageId, message)
                : AssistantMessage.AsError(messageId, message);

            m_Conversation.Messages.Add(closeMessage);

            // Mark the current assistant message as complete since we're closing
            m_AssistantMessage.IsComplete = true;

            // Release the incomplete-message tracking.
            m_IncompleteMessageCompleted?.Invoke(m_Conversation.Id);

            if (isInformational)
            {
                ReportTurnEnded("session_ended", null);
            }
            else
            {
                string failureReason;
                if (reason.Reason == CloseReason.ReasonType.AuthenticationFailed)
                    failureReason = "auth";
                else if (isTransportOrNetwork)
                    failureReason = "connection_lost";
                else if (reason.Reason == CloseReason.ReasonType.ServerDisconnected
                    || reason.Reason == CloseReason.ReasonType.ServerSentUnknownMessage
                    || reason.Reason == CloseReason.ReasonType.ServerSentMessageAtWrongTime)
                    failureReason = "server_disconnect";
                else
                    failureReason = "unknown";

                ReportTurnEnded("error", failureReason);
            }

            // Notify UI of the change
            m_NotifyConversationChange?.Invoke(m_Conversation);

            // Don't upload "bad-close" traces for informational disconnects (e.g. server graceful
            // shutdown for maintenance) — those are an expected, non-error condition.
            if (!isInformational)
            {
                TracesUploader.UploadTraces(m_Conversation.Id.Value, "bad-close");
            }
        }

        void HandleChatResponse(ChatResponseFragment fragment)
        {
            if (!m_FirstChunkSeen && m_PromptSentAt > 0)
            {
                m_FirstChunkSeen = true;
                var ttftMs = Math.Max(0L, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m_PromptSentAt);
                // Snapshot ids before dispatching to the main thread to avoid racing with
                // any later mutation of m_AssistantMessage.
                var conversationIdSnapshot = m_Conversation.Id;
                var messageIdSnapshot = m_AssistantMessage.Id;
                MainThread.DispatchAndForget(() =>
                    AIAssistantAnalytics.ReportUserMessageTtftEvent(conversationIdSnapshot, messageIdSnapshot, ttftMs));
            }

            try
            {
                fragment.Parse(m_Conversation.Id, m_AssistantMessage, m_ResponseBuilder);

                if (fragment.UsedTokens.HasValue)
                    m_Conversation.ContextUsageUsedTokens = fragment.UsedTokens.Value;
                if (fragment.MaxTokens.HasValue)
                    m_Conversation.ContextUsageMaxTokens = fragment.MaxTokens.Value;
                if (fragment.IsLastFragment && (m_Conversation.ContextUsageUsedTokens > 0 || m_Conversation.ContextUsageMaxTokens > 0))
                    Assistant.SaveContextUsage(m_Conversation.Id.Value, m_Conversation.ContextUsageUsedTokens, m_Conversation.ContextUsageMaxTokens);
            }
            catch (Exception e)
            {
                InternalLog.LogError($"[HandleChatResponse] Error parsing fragment during message recovery: {e}");

                if (fragment.IsLastFragment)
                {
                    m_AssistantMessage.IsComplete = true;
                    var conversationId = new AssistantConversationId(m_Conversation.Id.Value);
                    m_IncompleteMessageCompleted?.Invoke(conversationId);
                    Unsubscribe();
                    ReportTurnEnded("error", "client_parse_error");
                }

                m_NotifyConversationChange?.Invoke(m_Conversation);
                TracesUploader.UploadTraces(m_Conversation.Id.Value, "parse-fragment-exception");
                return;
            }

            if (fragment.IsLastFragment)
            {
                var lastSnippet = fragment.Fragment?.Length > 200 ? fragment.Fragment[..200] + "..." : fragment.Fragment;
                InternalLog.Log($"<color=orange>[HandleChatResponse]</color> <color=#CC3333>LastFragment</color> ({fragment.Fragment?.Length} chars): {lastSnippet}");
                m_AssistantMessage.IsComplete = true;
                m_AssistantMessage.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                ReportTurnEnded("completed", null);

                // Notify that incomplete message is complete (for domain reload tracking)
                var conversationId = new AssistantConversationId(m_Conversation.Id.Value);
                m_IncompleteMessageCompleted?.Invoke(conversationId);
                Unsubscribe();

                if (m_IsNewConversation)
                {
                    // TODO: Remove this dispatch when REST is replaced or changed to HttpClient that can be in background threads.
                    MainThread.DispatchAndForget(() =>
                    {
                        m_NotifyConversationChange?.Invoke(m_Conversation);
                    });
                }
            }
            else
            {
                var snippet = fragment.Fragment?.Length > 300 ? fragment.Fragment[..300] + "..." : fragment.Fragment;
                InternalLog.Log($"<color=orange>[HandleChatResponse]</color> Fragment ({fragment.Fragment?.Length} chars): {snippet}");
            }

            m_NotifyConversationChange?.Invoke(m_Conversation);
        }

        void OnWorkflowStateChange(State newState)
        {
            var conversationID = new AssistantConversationId(m_Workflow.ConversationId);
            switch (newState)
            {
                case State.NotStarted:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.NotConnected, $"Conversation {conversationID} has not yet started");
                    break;
                case State.AwaitingDiscussionInitialization:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.Connecting, $"Conversation {conversationID} is awaiting discussion initialization");
                    break;
                case State.Idle:
                    if (!m_Workflow.MessagesSent)
                        m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.AwaitingServer, $"Conversation {conversationID} is waiting for the server to reply to a prompt.");
                    else
                        m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.Connected, $"Conversation {conversationID} is connected and ready.");
                    break;
                case State.AwaitingChatAcknowledgement:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.AwaitingServer, $"Conversation {conversationID} is waiting for the server to reply to a prompt.");
                    break;
                case State.AwaitingChatResponse:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.AwaitingClient, $"Conversation {conversationID} is constructing context with the server.");
                    break;
                case State.ProcessingStream:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.AwaitingServer, $"Conversation {conversationID} is streaming a message from the server.");
                    break;
                case State.Canceling:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.Canceling, $"User elected to cancel request on conversation {conversationID}");
                    break;
                case State.Closed:
                    m_ChangePromptState?.Invoke(conversationID, Assistant.PromptState.NotConnected, $"Conversation {conversationID}'s websocket has closed.  A new websocket must be created.");
                    break;
            }
        }
    }

    internal partial class Assistant
    {
        readonly IDictionary<AssistantConversationId, AssistantConversation> m_ConversationCache =
            new Dictionary<AssistantConversationId, AssistantConversation>();

        public enum PromptState
        {
            NotConnected,
            Connecting,
            Connected,
            AwaitingServer,
            AwaitingClient,
            Canceling
        }

        internal PromptState CurrentPromptState { get; private set; }

        public event Action<AssistantConversationId, PromptState> PromptStateChanged;

        CancellationTokenSource m_ConnectionCancelToken;

        class PromptContext
        {
            public CredentialsContext Credentials;

            public AssistantContextEntry[] Asset;

            public List<ChatRequestV1.AttachedContextModel> Attached;
        }

        void ChangePromptState(AssistantConversationId conversationId, PromptState newState, string message, bool force = false)
        {
            if (CurrentPromptState == newState && !force)
            {
                return;
            }
            
            InternalLog.Log($"Changing state from {CurrentPromptState} to {newState} because {message}");
            CurrentPromptState = newState;
            PromptStateChanged?.Invoke(conversationId, newState);
            
            if (newState == PromptState.Canceling && 
                m_ConversationCache[conversationId]?.Messages.Count > 0 && 
                m_ConversationCache[conversationId]?.Messages[^1].Role == "assistant")
            {
                m_ConversationCache[conversationId].Messages[^1].IsComplete = true;
            }
        }

        public void AbortPrompt(AssistantConversationId conversationId)
        {
            if (CurrentPromptState is PromptState.Canceling or PromptState.NotConnected)
            {
                InternalLog.LogWarning($"AbortPrompt: Ignored in state {CurrentPromptState}");
                ChangePromptState(conversationId, PromptState.NotConnected, "Enforcing Not Connected on Abort", true);
                return;
            }

            m_ConnectionCancelToken?.Cancel();

            // Orchestration uses workflows to manage the connection to the backend rather than the stream object.
            // When orchestration is the only system, the stream objects will be removed.
            if (Backend is BaseWebSocketBackend webSocketBackend)
            {
                var workflow = webSocketBackend.ActiveWorkflow;
                if (workflow != null && workflow.ConversationId == conversationId.Value)
                    workflow.CancelCurrentChatRequest();

                webSocketBackend.ForceDisconnectWorkflow(conversationId.Value);
                ChangePromptState(conversationId, PromptState.NotConnected, "User cancelled the prompt. Disconnected workflow instantly.");
            }
        }

        public void DisconnectWorkflow()
        {
            if (Backend is BaseWebSocketBackend webSocketBackend)
            {
                webSocketBackend.ActiveWorkflow?.LocalDisconnect();
            }
        }

        public async Task ProcessPrompt(
            AssistantConversationId conversationId,
            AssistantPrompt prompt,
            IAgent agent = null,
            CancellationToken ct = default)
        {
            // Warm up ScriptableSingleton from main thread, or it
            // will throw exceptions later when we access it, and it initializes itself from a thread later on:
            var _ = AssistantEnvironment.WebSocketApiUrl;

            // It's possible that here the conversationId won't be valid because this is a new prompt. It doesn't
            // matter. The current prompt needs to be considered connecting the moment that processing begins to start
            // connecting it to the backend. Otherwise, there are timing gaps where features don't work, because they
            // are not aware that a prompt has started processing.
            ChangePromptState(
                conversationId, 
                PromptState.Connecting,
                "Connecting");
            
            var promptContext = new PromptContext { Credentials = await CredentialsProvider.GetCredentialsContext(ct) };
            TracesUploader.CacheCredentials(promptContext.Credentials);

            // Prepare serialized context, this needs to be on the main thread for asset db checks:
            promptContext.Asset = ContextSerializationHelper
                .BuildPromptSelectionContext(prompt.ObjectAttachments, prompt.VirtualAttachments, prompt.ConsoleAttachments).m_ContextList
                .ToArray();

            // Ensure the prompt adheres to the size constraints
            if (prompt.Value.Length > AssistantMessageSizeConstraints.PromptLimit)
            {
                prompt.Value = prompt.Value.Substring(0, AssistantMessageSizeConstraints.PromptLimit);
            }

            var attachedContext = PromptUtils.GetContextModel(AssistantMessageSizeConstraints.ContextLimit, prompt);
            promptContext.Attached = OrchestrationDataUtilities.FromEditorContextReport(attachedContext);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            TaskUtils.WithExceptionLogging(() => ProcessPromptInternal(conversationId, prompt, promptContext, agent, ct));
#pragma warning restore CS4014
        }

        public async Task RevertMessage(AssistantMessageId messageId)
        {
            if (messageId.ConversationId.IsValid)
            {
                var workflow = Backend.GetOrCreateWorkflow(await CredentialsProvider.GetCredentialsContext(), FunctionCaller, messageId.ConversationId);

                if (workflow != null)
                {
                    // Wait for the discussion to be initialized before sending the revert request
                    // This ensures the relay has established the cloud backend session
                    InternalLog.Log("[RevertMessage] Waiting for discussion initialization before sending revert request");
                    var isInitialized = await workflow.AwaitDiscussionInitialization();
                    if (!isInitialized)
                    {
                        InternalLog.LogError($"[RevertMessage] Failed to initialize workflow. {workflow.CloseReason}");
                        return;
                    }

                    workflow.RevertMessageRequest(messageId.FragmentId);
                }
            }
        }

        async Task ProcessPromptInternal(
            AssistantConversationId conversationId,
            AssistantPrompt prompt,
            PromptContext promptContext,
            IAgent agent = null,
            CancellationToken ct = default)
        {
            m_ConnectionCancelToken = new();
            var connectionCancelToken = m_ConnectionCancelToken.Token;

            // get the appropriate workflow
            var isNewConversation = !conversationId.IsValid;

            var workflow = Backend.GetOrCreateWorkflow(promptContext.Credentials, FunctionCaller, conversationId);

            await workflow.AwaitDiscussionInitialization();

            // If the user has cancelled the prompt, then treat this as an early-out
            if (CurrentPromptState == PromptState.Canceling)
            {
                InternalLog.LogWarning("ProcessPrompt: Early out due to user cancellation");
                return;
            }

            // A capacity close routes to the capacity-fallback UI (banner + provider switch), matching HandleClose,
            // rather than the generic error below.
            if (workflow.WorkflowState == State.Closed
                && workflow.CloseReason.Reason == CloseReason.ReasonType.ServerNoCapacity)
            {
                CapacityReached?.Invoke(conversationId);
                ChangePromptState(conversationId, PromptState.NotConnected, "The AI Assistant server is at capacity.");
                return;
            }

            // Pre-init there is no conversation yet, so any close other than an intentional client cancel means the
            // prompt never reached the server — surface it to recover the pending prompt. (Unlike HandleClose, which
            // treats graceful/informational closes as benign because it runs once a conversation exists.)
            if (workflow.WorkflowState == State.Closed
                && workflow.CloseReason.Reason != CloseReason.ReasonType.ClientCanceled)
            {
                ConversationErrorOccured?.Invoke(conversationId, new($"We were unable to establish communication with the AI Assistant server. {ErrorHandlingUtility.ErrorMessageNetworkedSuffix}", workflow.CloseReason.ToString()));
                ChangePromptState(conversationId, PromptState.NotConnected, "Unable to establish communication with the AI Assistant server.");
                return;
            }

            if (workflow.IsCancelled)
            {
                InternalLog.Log("ProcessPrompt: Early out due to workflow cancellation");
                return;
            }

            ChangePromptState(
                new AssistantConversationId(workflow.ConversationId),
                PromptState.Connected,
                "Connected");

            InternalLog.LogToFile(
                workflow.ConversationId,
                ("event", "processing prompt"),
                ("env", AssistantEnvironment.ApiUrl)
            );

            // Create the objects used by the UI code to render the conversation
            conversationId = new AssistantConversationId(workflow.ConversationId);

            if (!m_ConversationCache.TryGetValue(conversationId, out var conversation))
            {
                conversation = new AssistantConversation
                {
                    Title = AssistantConstants.DefaultConversationTitle,
                    Id = conversationId
                };

                m_ConversationCache.Add(conversationId, conversation);
            }

            // We should probably remove the need for the frontend to control this altogether, but as of right now
            // the frontend indicates when the title should be generated. It makes most sense to do this immediately
            // when the conversation id is available. This will result in eventually getting a title on the frontend.
            MainThread.DispatchAndForgetAsync(async () =>
            {
                var result = await Backend.ConversationGenerateTitle(
                    await CredentialsProvider.GetCredentialsContext(connectionCancelToken),
                    workflow.ConversationId, connectionCancelToken);

                if (!connectionCancelToken.IsCancellationRequested && result.Status == BackendResult.ResultStatus.Success && conversation != null)
                {
                    conversation.Title = result.Value;
                    NotifyConversationChange(conversation);
                }
            });

            // Add the messages needed to start rendering the response
            var promptMessage = AddInternalMessage(conversation, prompt.Value, role: k_UserRole, sendUpdate: true);
            promptMessage.Context = promptContext.Asset;

            var assistantMessage = AddIncompleteMessage(conversation, string.Empty, k_AssistantRole, sendUpdate: true);

            // Create checkpoint before any tool operations (if enabled)
            // Store as pending - will be tagged with real fragment ID when server responds
            if (AssistantProjectPreferences.CheckpointEnabled && AssistantCheckpoints.IsInitialized)
            {
                try
                {
                    var checkpointMessage = $"Before prompt: {TruncateForCheckpoint(prompt.Value)}";
                    var checkpointResult = await AssistantCheckpoints.CreateCheckpointAsync(checkpointMessage);
                    if (checkpointResult.Success)
                    {
                        AssistantCheckpoints.SetPendingCheckpoint(
                            assistantMessage.Id.ConversationId,
                            assistantMessage.Id.FragmentId,
                            checkpointResult.Value);
                    }
                }
                catch (Exception ex)
                {
                    InternalLog.LogWarning($"[Checkpoint] Failed to create checkpoint: {ex.Message}");
                }
            }

            // Track incomplete message for domain reload recovery
            IncompleteMessageStarted?.Invoke(conversationId, assistantMessage.Id.FragmentId);

            ToolInteractionAndPermissionBridge.ResetIgnoredObjects();
            if (isNewConversation)
            {
                ToolInteractionAndPermissionBridge.ResetTemporaryPermissions();
                ConversationCreated?.Invoke(conversation);
            }

            // Setup event handler
            StringBuilder assistantResponseStringBuilder = new();
            var eventHandler = new WorkflowEventHandler(
                workflow,
                conversation,
                assistantMessage,
                assistantResponseStringBuilder,
                connectionCancelToken,
                isNewConversation,
                ChangePromptState,
                ConversationErrorOccured,
                CapacityReached,
                NotifyConversationChange,
                (convId) => IncompleteMessageCompleted?.Invoke(convId));

            eventHandler.Subscribe();

            var originalPrompt = prompt.Value;
            var contextAnalyticsCache = prompt.ContextAnalyticsCache;
            var promptMode = prompt.Mode;

            workflow.OnAcknowledgeChat -= HandleChatAcknowledgment;
            workflow.OnAcknowledgeChat += HandleChatAcknowledgment;

            // Stamp t0 for client-side TTFT (prompt sent → first fragment arrival). Must happen
            // synchronously before the await so the timestamp reflects the actual send moment.
            eventHandler.SetPromptSentAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            await TaskUtils.WithExceptionLogging(() => workflow.SendChatRequest(prompt.Value, promptContext.Attached, agent, prompt.Mode, prompt.ModelConfiguration, ct));

            return;

            void HandleChatAcknowledgment(AcknowledgePromptInfo info)
            {
                workflow.OnAcknowledgeChat -= HandleChatAcknowledgment;

                promptMessage.Id = new AssistantMessageId(conversation.Id, info.Id, AssistantMessageIdType.External);
                promptMessage.Context = MergeContext(promptMessage.Context, info.Context);

                if (promptMessage.Blocks.Count != 1)
                    throw new Exception("Prompt message is expected to have a single block");

                if (promptMessage.Blocks[^1] is not PromptBlock promptBlock)
                    throw new Exception("Last block in prompt message is not a prompt block and should be during acknowledgment.");

                promptBlock.Content = info.Content;
                NotifyConversationChange(conversation);

                PendingCostUserMessageId = promptMessage.Id;

                // Report send event and flush all pending context attach events with the real backend message ID
                MainThread.DispatchAndForget(() =>
                {
                    contextAnalyticsCache?.FlushAll(promptMessage.Id);
                    AIAssistantAnalytics.ReportUserMessageSentEvent(originalPrompt, promptMessage.Id, promptMode);
                });
            }

            static AssistantContextEntry[] MergeContext(AssistantContextEntry[] localContext, AssistantContextEntry[] ackContext)
            {
                if ((localContext == null || localContext.Length == 0) &&
                    (ackContext == null || ackContext.Length == 0))
                    return Array.Empty<AssistantContextEntry>();

                if (localContext == null || localContext.Length == 0)
                    return ackContext;

                if (ackContext == null || ackContext.Length == 0)
                    return localContext;

                var merged = ackContext.ToList();
                foreach (var localEntry in localContext)
                {
                    if (!merged.Contains(localEntry))
                        merged.Add(localEntry);
                }

                return merged.ToArray();
            }
        }

        /// <summary>
        /// Resume an incomplete message after domain reload. Handles lifecycle of replayed and new streaming messages.
        /// </summary>
        void ResumeIncompleteMessage(
            IChatWorkflow workflow,
            AssistantConversation conversation,
            AssistantMessage assistantMessage,
            CancellationToken ct = default)
        {
            InternalLog.LogToFile(conversation.Id.ToString(), ("event", "resuming incomplete message"), ("blocks", assistantMessage.Blocks.Count.ToString()), ("isComplete", assistantMessage.IsComplete.ToString()));

            m_ConnectionCancelToken = new();
            var connectionCancelToken = m_ConnectionCancelToken.Token;

            var content = string.Empty;
            if (assistantMessage.Blocks.Count > 0 && assistantMessage.Blocks[^1] is AnswerBlock { IsComplete: false } responseBlock)
                content  = responseBlock.Content;

            // Initialize StringBuilder with existing content
            StringBuilder assistantResponseStringBuilder = new(content);

            // Setup event handler (no new conversation, no credentials for title generation)
            var eventHandler = new WorkflowEventHandler(
                workflow,
                conversation,
                assistantMessage,
                assistantResponseStringBuilder,
                connectionCancelToken,
                isNewConversation: false, // Resume means conversation already exists
                ChangePromptState,
                ConversationErrorOccured,
                CapacityReached,
                NotifyConversationChange,
                (convId) => IncompleteMessageCompleted?.Invoke(convId));

            eventHandler.Subscribe();

            // Don't send any request - just listen for replayed/streamed messages
        }

        static string TruncateForCheckpoint(string text, int maxLength = 50)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            text = text.Replace("\n", " ").Replace("\r", "");
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }
    }
}
