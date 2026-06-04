# Shared DTOs, enums, and request/response contracts

> GitHub: #3  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
This epic populates `FoundryGate.Contracts` with every DTO, enum, and wrapper type that the API and Blazor WASM client share. Defining contracts early and in one place prevents the API and frontend from diverging and makes it trivial to generate a typed HTTP client for the UI later. All types are plain C# records (immutable, with positional constructors where sensible), live in the `FoundryGate.Contracts` project, and carry no framework dependencies.

## Approach

### Define request/response DTOs for all API endpoint groups (#24)
Create one subfolder per domain (`Users/`, `Groups/`, `Quota/`, `Requests/`, `Keys/`, `Audit/`, `Config/`) under `src/FoundryGate.Contracts/`. Each subfolder contains request records (e.g., `CreateGroupRequest`, `SubmitQuotaIncreaseRequest`) and response records (e.g., `UserResponse`, `QuotaAllocationResponse`, `ApiKeyResponse`). Use `record` types with `init`-only properties. Annotate request records with `System.ComponentModel.DataAnnotations` attributes (`[Required]`, `[Range]`, `[MaxLength]`) so the ASP.NET Core model binder can validate them without extra dependencies.

Files expected to be created or modified:
- `src/FoundryGate.Contracts/Users/UserResponse.cs`
- `src/FoundryGate.Contracts/Users/UpdateUserRequest.cs`
- `src/FoundryGate.Contracts/Groups/GroupResponse.cs`
- `src/FoundryGate.Contracts/Groups/CreateGroupRequest.cs`
- `src/FoundryGate.Contracts/Groups/UpdateGroupRequest.cs`
- `src/FoundryGate.Contracts/Quota/QuotaAllocationResponse.cs`
- `src/FoundryGate.Contracts/Quota/QuotaIncreaseRequestResponse.cs`
- `src/FoundryGate.Contracts/Quota/SubmitQuotaIncreaseRequest.cs`
- `src/FoundryGate.Contracts/Keys/ApiKeyResponse.cs`
- `src/FoundryGate.Contracts/Audit/AuditLogResponse.cs`
- `src/FoundryGate.Contracts/Config/SystemConfigResponse.cs`
- `src/FoundryGate.Contracts/Config/UpdateSystemConfigRequest.cs`

### Define enums, shared constants, and paged-result wrapper (#25)
Add an `Enums/` folder with `UserStatus`, `RequestStatus`, `KeyStatus`, `AuditAction`, and `QuotaLevel`. Add a `Common/` folder with a generic `PagedResult<T>` record (items list, total count, page, page size) and a `ProblemDetails`-compatible error envelope for consistent API error shapes. Add a `Constants.cs` class with string constants for role names (`Roles.Admin`, `Roles.Developer`) and policy names.

Files expected to be created or modified:
- `src/FoundryGate.Contracts/Enums/UserStatus.cs`
- `src/FoundryGate.Contracts/Enums/RequestStatus.cs`
- `src/FoundryGate.Contracts/Enums/KeyStatus.cs`
- `src/FoundryGate.Contracts/Enums/AuditAction.cs`
- `src/FoundryGate.Contracts/Enums/QuotaLevel.cs`
- `src/FoundryGate.Contracts/Common/PagedResult.cs`
- `src/FoundryGate.Contracts/Common/ApiError.cs`
- `src/FoundryGate.Contracts/Constants.cs`

## Verification
- [ ] `dotnet build` passes with zero warnings
- [ ] `FoundryGate.Contracts` has no dependencies on ASP.NET Core or EF Core packages
- [ ] All request DTOs have at least one validation attribute
- [ ] `PagedResult<T>` is usable from both the API and the Blazor WASM project
