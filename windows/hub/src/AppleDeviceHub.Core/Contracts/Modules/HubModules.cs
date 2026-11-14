namespace AppleDeviceHub.Core.Contracts.Modules;

// Module boundaries for the ecosystem roadmap (ECOSYSTEM-ROADMAP.md section 4). Phase one
// defines these as interfaces only; there are no implementations, and the UI does not show them.

public enum ModuleAvailability
{
    /// <summary>Planned; no code path exists.</summary>
    NotImplemented = 0,

    /// <summary>Implemented but cannot run right now (missing device, permission, or driver).</summary>
    Unavailable = 1,

    /// <summary>Running on synthetic data only.</summary>
    Demo = 2,

    Available = 3,
    Faulted = 4,
}

public sealed record ModuleStatus(string ModuleId, string DisplayName, ModuleAvailability Availability, string Explanation);

/// <summary>Common surface for every hub module. A module can fail without affecting the others.</summary>
public interface IHubModule
{
    string ModuleId { get; }
    ModuleStatus Status { get; }
    event EventHandler<ModuleStatus>? StatusChanged;
}

// ---- iPhone messaging (MAP/OBEX over Bluetooth) ---------------------------------------

/// <summary>
/// A recipient the user chose explicitly (typed, or a specific address from a contact).
/// Never produced by fuzzy name matching: a wrong-recipient send is a release blocker.
/// </summary>
public sealed record ExplicitRecipient(string Address, string? DisplayName);

public enum MessageSendState
{
    /// <summary>The user pressed Send; nothing has left this PC yet.</summary>
    Requested = 0,

    /// <summary>The phone accepted the message for sending (for example, MAP PushMessage succeeded).</summary>
    AcceptedByPhone = 1,

    /// <summary>Refused. Nothing is retried automatically.</summary>
    Failed = 2,

    /// <summary>Contact was lost mid-send. Do NOT retry automatically: a retry could duplicate the message.</summary>
    Unknown = 3,

    /// <summary>Only when the phone actually reports delivery; many transports never do.</summary>
    DeliveryConfirmed = 4,
}

public sealed record MessageSendResult(Guid SendId, MessageSendState State, string? Detail);

/// <summary>A message this app actually observed. Not a claim about the phone's full history.</summary>
public sealed record ObservedMessage(
    string ConversationKey,
    string CounterpartAddress,
    string? CounterpartName,
    string Body,
    DateTimeOffset ObservedAt,
    bool IsOutgoing);

[Flags]
public enum MessagingCapabilities
{
    None = 0,
    ReadIncoming = 1 << 0,
    SendText = 1 << 1,
    ContactNames = 1 << 2,
    IncomingNotifications = 1 << 3,
}

/// <summary>Planned iPhone Bluetooth messaging module. NOT IMPLEMENTED in phase one.</summary>
public interface IPhoneMessagingModule : IHubModule
{
    MessagingCapabilities Capabilities { get; }

    event EventHandler<ObservedMessage>? MessageObserved;

    /// <summary>
    /// One attempt only, to exactly <paramref name="recipient"/>. Implementations must not
    /// retry automatically when the outcome is uncertain.
    /// </summary>
    Task<MessageSendResult> SendTextAsync(
        Guid sendId, ExplicitRecipient recipient, string body, CancellationToken cancellationToken = default);
}

// ---- iPhone notifications (ANCS) -----------------------------------------------------

public sealed record MirroredNotification(
    string NotificationId, string AppIdentifier, string Title, string Message, DateTimeOffset ObservedAt);

/// <summary>Planned notification mirror. NOT IMPLEMENTED in phase one.</summary>
public interface INotificationMirrorModule : IHubModule
{
    event EventHandler<MirroredNotification>? NotificationAdded;
    event EventHandler<string>? NotificationRemoved;
}

// ---- Local sharing (companion app over the local network) ----------------------------

public enum ShareKind { Link = 0, Text = 1, File = 2 }

public sealed record ShareOffer(Guid OfferId, ShareKind Kind, string SenderDeviceName, string Summary, long? SizeBytes);

public enum ShareDecision { Accept = 0, Decline = 1 }

/// <summary>Planned local sharing. Nothing is saved or opened until the user accepts.</summary>
public interface ILocalSharingModule : IHubModule
{
    event EventHandler<ShareOffer>? OfferReceived;

    Task RespondAsync(Guid offerId, ShareDecision decision, CancellationToken cancellationToken = default);
}

// ---- Apple Watch remote (forwarded by the iPhone companion) ---------------------------

public enum WatchCommandKind { PlayPause = 0, VolumeUp = 1, VolumeDown = 2, NextSlide = 3, PreviousSlide = 4, PlayLocateSound = 5 }

public sealed record WatchCommand(Guid CommandId, WatchCommandKind Kind, DateTimeOffset IssuedAt);

public enum WatchCommandOutcome { Performed = 0, Rejected = 1, Unavailable = 2 }

/// <summary>Planned watch remote: a small authenticated command set. NOT IMPLEMENTED in phase one.</summary>
public interface IWatchCommandModule : IHubModule
{
    /// <summary>
    /// Register the single handler that performs an authenticated command on the PC and
    /// reports what actually happened, so the watch can show an honest result.
    /// </summary>
    void SetHandler(Func<WatchCommand, CancellationToken, Task<WatchCommandOutcome>> handler);
}
