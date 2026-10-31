// Foreground live delivery over the SignalR hub, like the desktop agent. The app only keeps the connection while it is
// in the foreground; backgrounded it disconnects so the server hands deliveries to the push provider instead.
import { HubConnection, HubConnectionBuilder, HubConnectionState, HttpTransportType, LogLevel } from '@microsoft/signalr';
import { NotificationMessage } from './api';
import { appVersion, Pairing, platform, TenantInfo } from './config';
import { PushState } from './push';

export type HelloResponse = { tenant: TenantInfo; endpointId: string; name: string; subscriptions: string[]; serverTime: string; pendingReplay: number };
export type ConnectionState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

export type RealtimeEvents = {
  onState: (state: ConnectionState, detail?: string) => void;
  onHello: (hello: HelloResponse) => void;
  onNotification: (msg: NotificationMessage) => void;
  onSubscriptionsChanged: (tags: string[]) => void;
  onCredentialRevoked: (reason: string) => void;
};

export class Realtime {
  private connection: HubConnection | null = null;
  private push: PushState = { provider: null, token: null, reason: 'not requested' };

  constructor(private readonly pairing: Pairing, private readonly events: RealtimeEvents) {}

  setPush(push: PushState) { this.push = push; }

  get state(): ConnectionState {
    switch (this.connection?.state) {
      case HubConnectionState.Connected: return 'connected';
      case HubConnectionState.Connecting: return 'connecting';
      case HubConnectionState.Reconnecting: return 'reconnecting';
      default: return 'disconnected';
    }
  }

  async start(): Promise<void> {
    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return;
    const c = new HubConnectionBuilder()
      .withUrl(`${this.pairing.serverUrl}/hubs/notifications`, {
        accessTokenFactory: () => this.pairing.token,
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
    c.on('NotificationReceived', (msg: NotificationMessage) => this.events.onNotification(msg));
    c.on('SubscriptionsChanged', (tags: string[]) => this.events.onSubscriptionsChanged(tags));
    c.on('CredentialRevoked', (reason: string) => this.events.onCredentialRevoked(reason));
    c.onreconnecting((err) => this.events.onState('reconnecting', err?.message));
    c.onreconnected(() => { void this.hello(); });
    c.onclose((err) => this.events.onState('disconnected', err?.message));
    this.connection = c;
    this.events.onState('connecting');
    try {
      await c.start();
      await this.hello();
    } catch (e) {
      this.events.onState('disconnected', (e as Error).message);
    }
  }

  private async hello(): Promise<void> {
    const c = this.connection;
    if (!c || c.state !== HubConnectionState.Connected) return;
    try {
      const hello = await c.invoke<HelloResponse>('Hello', {
        endpointId: this.pairing.endpointId, name: this.pairing.deviceName, platform, agentVersion: appVersion,
        pushProvider: this.push.provider, pushToken: this.push.token,
      });
      this.events.onState('connected');
      this.events.onHello(hello);
    } catch (e) {
      this.events.onState('disconnected', (e as Error).message);
      await this.stop();
    }
  }

  async stop(): Promise<void> {
    const c = this.connection;
    this.connection = null;
    if (c) { try { await c.stop(); } catch { /* already closed */ } }
    this.events.onState('disconnected');
  }

  async ack(deliveryId: string): Promise<boolean> {
    const c = this.connection;
    if (!c || c.state !== HubConnectionState.Connected) return false;
    try { await c.invoke('Ack', deliveryId); return true; } catch { return false; }
  }
}
