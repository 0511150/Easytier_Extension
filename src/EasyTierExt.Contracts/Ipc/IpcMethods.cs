namespace EasyTierExt.Contracts.Ipc;

public static class IpcMethods
{
    // Plugin -> Host Requests
    public const string PluginHello = "plugin.hello";
    public const string HostGetNetworks = "host.getNetworks";
    public const string HostGetPeers = "host.getPeers";
    public const string DiscoveryRegister = "discovery.register";
    public const string DiscoveryUpdateMeta = "discovery.updateMeta";
    public const string DiscoveryGetServicePeers = "discovery.getServicePeers";
    public const string UiSetSchema = "ui.setSchema";
    public const string UiUpdateValues = "ui.updateValues";
    public const string UiNavigateTo = "ui.navigateTo";
    public const string UiGetViewportHost = "ui.getViewportHost";

    // Host -> Plugin Notifications
    public const string EventNetworksChanged = "event.networksChanged";
    public const string EventPeersUpdated = "event.peersUpdated";
    public const string EventDiscoveryUpdated = "event.discoveryUpdated";
    public const string EventUiAction = "event.uiAction";
    public const string EventViewportChanged = "event.viewportChanged";
    public const string EventShutdown = "event.shutdown";
}
