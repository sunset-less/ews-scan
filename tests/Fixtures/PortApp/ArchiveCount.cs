using Microsoft.Exchange.WebServices.Data;

namespace PortApp;

public class ArchiveCount(ExchangeService service)
{
    public async Task<int> CountAsync()
    {
        var folder = await Folder.Bind(service, WellKnownFolderName.ArchiveRoot);
        var found = await service.FindItems(folder.Id, new ItemView(5));
        return found.TotalCount;
    }
}
