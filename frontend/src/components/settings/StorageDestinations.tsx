import { useState } from "react";

import type { StorageCredentialMetadata, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";
import type { RoutedDataClass, StorageRouteSummary } from "@/api/storageRoutes";

import { FixConnectionDialog } from "./FixConnectionDialog";
import { StorageDestinationCard } from "./StorageDestinationCard";
import { WhatLandsHereDialog } from "./WhatLandsHereDialog";

/**
 * The places this team's data is kept, one card each.
 *
 * A retired destination is deliberately absent. Retirement is terminal and only allowed once nothing is stored under
 * it, so a retired card would be a row about a place that holds nothing and can never hold anything — the kind of
 * permanent debris this whole surface exists to stop accumulating. Its history is still reachable through the
 * step-by-step flow, which is where the lifecycle lives.
 */
export function StorageDestinations({ profiles, providers, credentials, routes, dataClasses, mayManage, loading, error, onAdvanced }: {
  profiles: StorageProfileSummary[];
  providers: StorageProviderModuleSummary[];
  credentials: StorageCredentialMetadata[];
  routes: StorageRouteSummary[];
  dataClasses: RoutedDataClass[];
  mayManage: boolean;
  loading: boolean;
  /** A load failure, which is NOT an empty team: saying "nothing is set up yet" about an unread list invites setting up a second of everything. */
  error: string | null;
  onAdvanced: (profileId: string) => void;
}) {
  const [fixing, setFixing] = useState<string | null>(null);
  const [routing, setRouting] = useState<string | null>(null);

  const live = profiles.filter((profile) => profile.state !== "Retired");
  const fixingProfile = live.find((profile) => profile.id === fixing);
  const routingProfile = live.find((profile) => profile.id === routing);

  if (loading) return <div className="ct-empty" role="status"><div className="ct-empty-h">Loading&hellip;</div></div>;

  if (error != null) {
    return (
      <div className="cn-banner cn-banner-err" role="alert">
        <div className="cn-banner-h">{"Couldn't load where this team's data is kept"}</div>
        <div className="cn-banner-p">{error}</div>
        <div className="cn-banner-p">This is not the same as having none. Nothing has been changed, and adding a destination now could duplicate one that already exists.</div>
      </div>
    );
  }

  if (live.length === 0) {
    return (
      <div className="ct-empty">
        <div className="ct-empty-h">Nothing is set up yet</div>
        <div className="ct-empty-p">Connect a place to keep this team&rsquo;s data, and choose what lands in it.</div>
      </div>
    );
  }

  return (
    <>
      <div role="list" aria-label="Where this team's data is kept">
      {live.map((profile) => (
        <StorageDestinationCard
          key={profile.id}
          profile={profile}
          providers={providers}
          credentials={credentials}
          routes={routes}
          dataClasses={dataClasses}
          mayManage={mayManage}
          onFix={() => setFixing(profile.id)}
          onEditRouting={() => setRouting(profile.id)}
          onAdvanced={() => onAdvanced(profile.id)}
        />
      ))}
      </div>

      {fixingProfile && (
        <FixConnectionDialog
          profile={fixingProfile}
          provider={providers.find((candidate) => candidate.typeKey === fixingProfile.providerTypeKey)}
          credentials={credentials}
          onClose={() => setFixing(null)}
        />
      )}

      {routingProfile && (
        <WhatLandsHereDialog
          profile={routingProfile}
          routes={routes}
          dataClasses={dataClasses}
          onClose={() => setRouting(null)}
        />
      )}
    </>
  );
}
