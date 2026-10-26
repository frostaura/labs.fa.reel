import type { ProvidersPayload } from "../store/titleTypes";

/**
 * A RECORDED where-to-watch payload, not a hand-written one: "Seized" (2020, TMDB 572751) in the US,
 * the title ireview_surface defect 4 walked. Built from the eval copy's `TitleAvailabilities` rows
 * (fetched from TMDB 2026-10-22, slot database reel_ws10a) through `ProviderEndpoints`' own
 * projection: one entry per provider in display-priority order, `linkKind: "direct"` exactly where
 * the endpoint has a maintained search pattern (Google Play, YouTube) and the TMDB watch page
 * everywhere else. Recorded rather than live, so TMDB's data drift (FD-13) cannot move the tests.
 */
export const SEIZED_US_PROVIDERS: ProvidersPayload = {
  region: "US",
  attribution: "Watch-provider data powered by JustWatch via TMDB.",
  tmdbWatchPage: "https://www.themoviedb.org/movie/572751/watch?locale=US",
  providers: [
    { provider: "Amazon Video", logoPath: "/qR6FKvnPBx2O37FDg8PNM7efwF3.jpg", kinds: ["Buy", "Rent"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 8 },
    { provider: "Apple TV Store", logoPath: "/SPnB1qiCkYfirS2it3hZORwGVn.jpg", kinds: ["Buy", "Rent"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 9 },
    { provider: "Philo", logoPath: "/ptmbGSttkyzawLbxx9MElmxKuVo.jpg", kinds: ["Flatrate"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 17 },
    { provider: "Google Play Movies", logoPath: "/8z7rC8uIDaTM91X0ZfkRf04ydj2.jpg", kinds: ["Buy", "Rent"], linkKind: "direct", url: "https://play.google.com/store/search?q=Seized&c=movies", displayPriority: 20 },
    { provider: "YouTube", logoPath: "/pTnn5JwWr4p3pG8H6VrpiQo7Vs0.jpg", kinds: ["Buy", "Rent"], linkKind: "direct", url: "https://www.youtube.com/results?search_query=Seized", displayPriority: 21 },
    { provider: "Starz Apple TV Channel", logoPath: "/1C5EVCWyQD798CE1DFfcm6oAbxP.jpg", kinds: ["Flatrate"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 24 },
    { provider: "The Roku Channel", logoPath: "/wQzSN83BnWVgO7xEh0SeTVqtrFv.jpg", kinds: ["Ads"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 30 },
    { provider: "Starz Roku Premium Channel", logoPath: "/9laPF1MAiUxlqM8T98F3Gj0bhzd.jpg", kinds: ["Flatrate"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 31 },
    { provider: "Fandango At Home", logoPath: "/oIXE9vJdkilIxCLAtlbtiqewgvn.jpg", kinds: ["Buy", "Rent"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 39 },
    { provider: "Starz", logoPath: "/yIKwylTLP1u8gl84Is7FItpYLGL.jpg", kinds: ["Flatrate"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 41 },
    { provider: "Fandango at Home Free", logoPath: "/oIXE9vJdkilIxCLAtlbtiqewgvn.jpg", kinds: ["Ads"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 42 },
    { provider: "Plex", logoPath: "/vLZKlXUNDcZR7ilvfY9Wr9k80FZ.jpg", kinds: ["Ads"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 126 },
    { provider: "Plex Channel", logoPath: "/27WMfRN7pQE3j5Khm8fPM7vYyLV.jpg", kinds: ["Ads"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 127 },
    { provider: "Fawesome", logoPath: "/pSUa7lMYLoQAU00ikXoHxmOfTZ9.jpg", kinds: ["Free"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 145 },
    { provider: "Starz Amazon Channel", logoPath: "/esiLBRzDUwodjfN8gA4qj7l3ZF7.jpg", kinds: ["Flatrate"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 153 },
    { provider: "MovieSphere+ Amazon Channel", logoPath: "/bEI5UgVjthdjiBXGiKRy7uSCRvF.jpg", kinds: ["Flatrate"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 291 },
    { provider: "Tubi TV", logoPath: "/zLYr7OPvpskMA4S79E3vlCi71iC.jpg", kinds: ["Ads"], linkKind: "tmdb", url: "https://www.themoviedb.org/movie/572751/watch?locale=US", displayPriority: 347 },
  ],
};
