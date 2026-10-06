using Microsoft.Exchange.WebServices.Data;

namespace ClassicApp;

public class Sync
{
    private readonly ExchangeService service = new(ExchangeVersion.Exchange2013_SP1)
    {
        Credentials = new WebCredentials("user", "password"),
    };

    public void ReadInbox()
    {
        var unread = new SearchFilter.IsEqualTo(EmailMessageSchema.IsRead, false);
        foreach (var item in service.FindItems(WellKnownFolderName.Inbox, unread, new ItemView(10)))
        {
            var message = EmailMessage.Bind(service, item.Id);
            message.Load(new PropertySet(ItemSchema.MimeContent));
        }
    }

    public Folder PublicRoot() => Folder.Bind(service, WellKnownFolderName.PublicFoldersRoot);

    public Folder Archive()
    {
        var id = new FolderId(WellKnownFolderName.ArchiveMsgFolderRoot, new Mailbox("archive@example.com"));
        return Folder.Bind(service, id);
    }

    public void QuietMeeting(Appointment appointment)
    {
        var mode = SendInvitationsMode.SendToNone;
        appointment.Save(mode);
        appointment.Delete(DeleteMode.HardDelete, SendCancellationsMode.SendToNone);
        service.DeleteItems(new[] { appointment.Id }, DeleteMode.HardDelete, SendCancellationsMode.SendToNone, null);
    }

    public void Invite(Appointment appointment) => appointment.Save(SendInvitationsMode.SendToAllAndSaveCopy);

    public async System.Threading.Tasks.Task ListenAsync()
    {
        await System.Threading.Tasks.Task.Yield();
        var subscription = service.SubscribeToStreamingNotifications(new FolderId[] { WellKnownFolderName.Inbox }, EventType.NewMail);
        var connection = new StreamingSubscriptionConnection(service, 30);
        connection.AddSubscription(subscription);
        connection.Open();
    }

    public IAsyncResult BeginSync(AsyncCallback callback) =>
        service.BeginSyncFolderItems(callback, null, WellKnownFolderName.Inbox, PropertySet.IdOnly, null, 100, SyncFolderItemsScope.NormalItems, null);

    public Func<ItemId, Item> Tasks()
    {
        var task = new Microsoft.Exchange.WebServices.Data.Task(service) { Subject = "Call back" };
        task.Save();
        return id => Item.Bind(service, id);
    }

    public string MarketplaceUrl() => service.GetAppMarketplaceUrl();
}
