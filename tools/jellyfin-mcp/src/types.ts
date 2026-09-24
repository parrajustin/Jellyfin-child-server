/**
 * The slice of the Jellyfin and child server API surface this MCP server uses.
 *
 * These mirror the C# models in `MediaBrowser.Model/ChildServer/`. Jellyfin serialises enums as
 * their names, so the status fields are string unions rather than numbers.
 */

export interface ParentRequestHeader {
  Name: string;
  Value: string;
}

export type ParentConnectionStatus =
  | 'Success'
  | 'InvalidCredentials'
  | 'AccessDenied'
  | 'ConnectionFailed'
  | 'InvalidResponse';

export type ChildSyncState = 'Idle' | 'Running';

export interface ChildServerSettings {
  ParentUrl?: string | null;
  Username?: string | null;
  /** Always null on the way out: the server never returns the stored password. */
  Password?: string | null;
  HasPassword: boolean;
  CustomHeaders: ParentRequestHeader[];
  PrefetchEpisodeCount: number;
  MaxCacheSizeMb: number;
  DownloadWaitTimeoutSeconds: number;
  SyncIntervalHours: number;
}

/** What POST /ChildServer/Configuration accepts. An empty password keeps the stored one. */
export interface ChildServerSettingsInput {
  ParentUrl?: string;
  Username?: string;
  Password?: string;
  CustomHeaders?: ParentRequestHeader[];
  PrefetchEpisodeCount?: number;
  MaxCacheSizeMb?: number;
  DownloadWaitTimeoutSeconds?: number;
  SyncIntervalHours?: number;
}

export interface ParentConnectionResult {
  Status: ParentConnectionStatus;
  Message: string;
  ServerName?: string | null;
  ServerVersion?: string | null;
  ServerId?: string | null;
  UserId?: string | null;
  IsSuccess: boolean;
}

export interface ChildServerStatus {
  IsConfigured: boolean;
  IsAuthenticated: boolean;
  ParentServerName?: string | null;
  ParentServerVersion?: string | null;
  ParentUserId?: string | null;
  LastConnectionStatus?: ParentConnectionStatus | null;
  LastConnectionMessage?: string | null;
  LastConnectionAttemptUtc?: string | null;
  SyncState: ChildSyncState;
  LastSyncStartedUtc?: string | null;
  LastSyncCompletedUtc?: string | null;
  LastSyncError?: string | null;
  MirroredItemCount: number;
  CachedItemCount: number;
  CachedBytes: number;
  ActiveDownloads: number;
}

export interface AuthenticationResult {
  AccessToken?: string;
  ServerId?: string;
  User?: { Id?: string; Name?: string; Policy?: { IsAdministrator?: boolean } };
}

export interface BaseItem {
  Id: string;
  Name: string;
  Type?: string;
  CollectionType?: string;
  Path?: string;
  RunTimeTicks?: number;
  SeriesName?: string;
}

export interface ItemsResult<T extends BaseItem = BaseItem> {
  Items: T[];
  TotalRecordCount: number;
}

export interface MediaSource {
  Id?: string;
  Container?: string;
  Size?: number;
  SupportsDirectPlay?: boolean;
  SupportsTranscoding?: boolean;
  MediaStreams?: Array<{ Type?: string; Codec?: string }>;
}

export interface PlaybackInfoResponse {
  MediaSources?: MediaSource[];
  PlaySessionId?: string;
  ErrorCode?: string | null;
}

export interface LogFile {
  Name: string;
  Size: number;
  DateCreated: string;
  DateModified: string;
}
