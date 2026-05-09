export type AppRoute =
  | { name: "dashboard" }
  | { name: "cluster" }
  | { name: "health" }
  | { name: "buckets" }
  | { name: "bucket"; bucketName: string }
  | { name: "access-keys" }
  | { name: "settings" }
  | { name: "audit" }

export function parseRoute(pathname: string): AppRoute {
  const segments = pathname.split("/").filter(Boolean).map(decodeURIComponent)
  if (segments[0] === "buckets" && segments[1]) {
    return { name: "bucket", bucketName: segments[1] }
  }

  if (segments[0] === "cluster") {
    return { name: "cluster" }
  }

  if (segments[0] === "health") {
    return { name: "health" }
  }

  if (segments[0] === "buckets") {
    return { name: "buckets" }
  }

  if (segments[0] === "access-keys") {
    return { name: "access-keys" }
  }

  if (segments[0] === "settings") {
    return { name: "settings" }
  }

  if (segments[0] === "audit") {
    return { name: "audit" }
  }

  return { name: "dashboard" }
}

export function routeHref(route: AppRoute): string {
  if (route.name === "bucket") {
    return `/buckets/${encodeURIComponent(route.bucketName)}`
  }

  if (route.name === "dashboard") {
    return "/"
  }

  return `/${route.name}`
}
