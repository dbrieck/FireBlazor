# Changelog

All notable changes to FireBlazor will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **`in` / `not-in` queries with array collections** - `.Where(x => array.Contains(x.Field))`
  where `array` is a `T[]` silently produced no filter, because arrays bind to the span-based
  `MemoryExtensions.Contains` overload (wrapping the array in an implicit `ReadOnlySpan<T>`
  conversion) rather than `Enumerable.Contains`. The predicate visitor now recognizes all
  `Contains` overloads and unwraps the conversion, so array-backed `in`/`not-in` filters and
  `array-contains` on array fields are translated correctly.

## [1.0.0] - 2025-01-15

### Added

- **Firebase Authentication** - Email/password, Google, GitHub, Microsoft OAuth providers
- **Cloud Firestore** - Full CRUD, LINQ-style queries, real-time subscriptions, transactions, batch operations, aggregate queries
- **Cloud Storage** - Upload/download with progress tracking, metadata management, file listing
- **Realtime Database** - CRUD operations, real-time listeners, presence detection, server values, transactions
- **App Check** - reCAPTCHA v3/Enterprise integration
- **Firebase AI Logic** - Gemini model integration with streaming, function calling, multimodal input, grounding
- **Result Pattern** - Functional error handling with `Result<T>`
- **Blazor Authorization** - Integration with `[Authorize]` and `<AuthorizeView>`
- **Emulator Support** - Full local development with Firebase Emulator Suite
- **Testing Infrastructure** - Fake implementations (`FakeFirebase`, `FakeAuth`, etc.)
- **FirebaseComponentBase** - Base component with automatic subscription cleanup

### Fixed

- Function call parameter serialization uses camelCase
- Google Search grounding configuration simplified
- `FunctionCalls` deserialization in `GenerateContentResponse`

[Unreleased]: https://github.com/user/FireBlazor/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/user/FireBlazor/releases/tag/v1.0.0
