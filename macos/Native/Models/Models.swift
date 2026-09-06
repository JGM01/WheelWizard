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

struct RuntimeSettings: Codable {
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
