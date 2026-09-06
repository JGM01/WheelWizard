import Combine

@MainActor
final class Activity: ObservableObject {
    @Published private(set) var log = [String]()
    @Published var message = ""
    @Published var progress: Double?

    func append(_ text: String) {
        log.append(text)
        if log.count > 500 {
            log.removeFirst(log.count - 500)
        }
    }
}
