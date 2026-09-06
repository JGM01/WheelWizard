import Foundation

struct SetupInput: Codable {
    var wbfs = ""
    var workspace = ""
    var cmake = ""
    var ninja = ""
    var nodtool = ""
    var translator = ""
}

struct Product: Identifiable, Codable {
    let id: String
    let name: String
    var ready: Bool
    var detail: String
}

struct RuntimeSettings: Codable, Equatable {
    var volume = 1.0
    var resolutionMultiplier = 1.0
}

struct PackageInfo {
    var installed = false
    var version = ""
    var latest = ""
    var outOfDate = false
    var serverReachable = true
}

struct ManagedMod: Codable, Identifiable {
    let title: String
    let author: String
    let modID: Int
    let isEnabled: Bool
    let priority: Int
    var findings: [CompatibilityFinding] = []
    var inspectionError: String?
    var requiresAttention: Bool { inspectionError != nil || !findings.isEmpty }
    var id: String { title }
}

struct ModFileSource: Codable {
    let modTitle: String
    let sourcePath: String
}

struct ModLaunchFile: Codable, Identifiable {
    let destination: String
    let winner: ModFileSource
    let overwritten: [ModFileSource]
    var id: String { destination }
}

// Slim GameBanana catalog rows/detail sent by the helper (see Host ModSearchProjection/ModDetailsProjection).
struct CatalogMod: Codable, Identifiable, Equatable {
    let id: Int
    let name: String
    let version: String
    let author: String
    let profileUrl: String
    let imageUrl: String?
    let likeCount: Int
    let viewCount: Int
    let usesPatches: Bool
    let tags: [String]
}

struct CatalogAuthor: Codable {
    let name: String
    let profileUrl: String
}

struct CatalogFile: Codable, Identifiable {
    let fileName: String
    let fileSize: Int
    let downloadUrl: String
    var id: String { downloadUrl }
}

struct CatalogModDetail: Codable {
    let id: Int
    let name: String
    let version: String
    let profileUrl: String
    let author: CatalogAuthor
    let likeCount: Int
    let viewCount: Int
    let downloadCount: Int
    let text: String
    let images: [String]
    let files: [CatalogFile]
    let archivedFiles: [CatalogFile]
}

struct CatalogSearchPage: Codable {
    let results: [CatalogMod]
    let isComplete: Bool
}

struct CompatibilityFinding: Codable {
    let modTitle: String
    let relativePath: String
    let reason: String
}

struct PatchRecovery: Codable {
    let target: String
    let recordPath: String
    let message: String
}
