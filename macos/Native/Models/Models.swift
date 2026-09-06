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
}
