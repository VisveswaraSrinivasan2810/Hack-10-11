using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace CivicLens.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicIncidentCode = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Location = table.Column<Point>(type: "geography (point)", nullable: false),
                    LocationLabel = table.Column<string>(type: "text", nullable: true),
                    PrimaryImageUrl = table.Column<string>(type: "text", nullable: true),
                    SeverityScore = table.Column<int>(type: "integer", nullable: false),
                    PriorityScore = table.Column<double>(type: "double precision", nullable: false),
                    PriorityBand = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResolutionStatus = table.Column<int>(type: "integer", nullable: false),
                    FirstObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ObservationCount = table.Column<int>(type: "integer", nullable: false),
                    ConfirmationCount = table.Column<int>(type: "integer", nullable: false),
                    RecurrencePriorCount = table.Column<int>(type: "integer", nullable: false),
                    AssignedWorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReopenCount = table.Column<int>(type: "integer", nullable: false),
                    SeverityOverridden = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ObservationDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    ImageHash = table.Column<string>(type: "text", nullable: false),
                    ImageSignature = table.Column<string>(type: "text", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    LocationLabel = table.Column<string>(type: "text", nullable: true),
                    DetectedCategory = table.Column<int>(type: "integer", nullable: false),
                    AiConfidence = table.Column<double>(type: "double precision", nullable: false),
                    SeverityScore = table.Column<int>(type: "integer", nullable: false),
                    AiReason = table.Column<string>(type: "text", nullable: true),
                    SuspectedDuplicateImage = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservationDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PointsOfInterest",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Location = table.Column<Point>(type: "geography (point)", nullable: false),
                    RiskWeight = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointsOfInterest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Email = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    TrustScore = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Zones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Boundary = table.Column<Polygon>(type: "geography (polygon)", nullable: false),
                    CenterLatitude = table.Column<double>(type: "double precision", nullable: false),
                    CenterLongitude = table.Column<double>(type: "double precision", nullable: false),
                    CivicHealthScore = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Zones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Assignments_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CitizenVerifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolutionEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Vote = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CitizenVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CitizenVerifications_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Correlations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceIncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetIncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrelationType = table.Column<string>(type: "text", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    DistanceMeters = table.Column<double>(type: "double precision", nullable: false),
                    Explanation = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Correlations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Correlations_Incidents_SourceIncidentId",
                        column: x => x.SourceIncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Correlations_Incidents_TargetIncidentId",
                        column: x => x.TargetIncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "History",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Message = table.Column<string>(type: "text", nullable: true),
                    OldStatus = table.Column<int>(type: "integer", nullable: true),
                    NewStatus = table.Column<int>(type: "integer", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Metadata = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_History", x => x.Id);
                    table.ForeignKey(
                        name: "FK_History_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentConfirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentConfirmations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentConfirmations_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriorityBreakdowns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Severity = table.Column<double>(type: "double precision", nullable: false),
                    CitizenEvidence = table.Column<double>(type: "double precision", nullable: false),
                    LocationRisk = table.Column<double>(type: "double precision", nullable: false),
                    IncidentAge = table.Column<double>(type: "double precision", nullable: false),
                    Exposure = table.Column<double>(type: "double precision", nullable: false),
                    Recurrence = table.Column<double>(type: "double precision", nullable: false),
                    ContextAmplifier = table.Column<double>(type: "double precision", nullable: false),
                    FinalScore = table.Column<double>(type: "double precision", nullable: false),
                    Band = table.Column<int>(type: "integer", nullable: false),
                    ReasonsJson = table.Column<string>(type: "text", nullable: false),
                    CalculatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriorityBreakdowns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PriorityBreakdowns_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResolutionEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    BeforeImageUrl = table.Column<string>(type: "text", nullable: true),
                    AfterImageUrl = table.Column<string>(type: "text", nullable: false),
                    AfterImageHash = table.Column<string>(type: "text", nullable: false),
                    WorkerNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AiConfidence = table.Column<double>(type: "double precision", nullable: false),
                    VerificationResult = table.Column<int>(type: "integer", nullable: false),
                    AiSummary = table.Column<string>(type: "text", nullable: true),
                    ResolutionConfidence = table.Column<double>(type: "double precision", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolutionEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResolutionEvidence_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: false),
                    ImageHash = table.Column<string>(type: "text", nullable: false),
                    ImageSignature = table.Column<string>(type: "text", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    Location = table.Column<Point>(type: "geography (point)", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DetectedCategory = table.Column<int>(type: "integer", nullable: false),
                    AiConfidence = table.Column<double>(type: "double precision", nullable: false),
                    CitizenSelectedCategory = table.Column<int>(type: "integer", nullable: false),
                    SeverityScore = table.Column<int>(type: "integer", nullable: false),
                    AiReason = table.Column<string>(type: "text", nullable: true),
                    SuspectedDuplicateImage = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidationStatus = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Observations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Observations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentObservations",
                columns: table => new
                {
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SameIncidentScore = table.Column<double>(type: "double precision", nullable: false),
                    MergeReason = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentObservations", x => new { x.IncidentId, x.ObservationId });
                    table.ForeignKey(
                        name: "FK_IncidentObservations_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IncidentObservations_Observations_ObservationId",
                        column: x => x.ObservationId,
                        principalTable: "Observations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_IncidentId",
                table: "Assignments",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Assignments_WorkerId",
                table: "Assignments",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_CitizenVerifications_IncidentId",
                table: "CitizenVerifications",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_CitizenVerifications_ResolutionEvidenceId_UserId",
                table: "CitizenVerifications",
                columns: new[] { "ResolutionEvidenceId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Correlations_SourceIncidentId_TargetIncidentId",
                table: "Correlations",
                columns: new[] { "SourceIncidentId", "TargetIncidentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Correlations_TargetIncidentId",
                table: "Correlations",
                column: "TargetIncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_History_IncidentId_CreatedAt",
                table: "History",
                columns: new[] { "IncidentId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentConfirmations_IncidentId_UserId",
                table: "IncidentConfirmations",
                columns: new[] { "IncidentId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentObservations_ObservationId",
                table: "IncidentObservations",
                column: "ObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Latitude_Longitude",
                table: "Incidents",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Location",
                table: "Incidents",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_PriorityScore",
                table: "Incidents",
                column: "PriorityScore");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_PublicIncidentCode",
                table: "Incidents",
                column: "PublicIncidentCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_Status",
                table: "Incidents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ObservationDrafts_UserId_CreatedAt",
                table: "ObservationDrafts",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Observations_ImageHash",
                table: "Observations",
                column: "ImageHash");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_Location",
                table: "Observations",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_Observations_UserId_CreatedAt",
                table: "Observations",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PointsOfInterest_Location",
                table: "PointsOfInterest",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_PriorityBreakdowns_IncidentId_CalculatedAt",
                table: "PriorityBreakdowns",
                columns: new[] { "IncidentId", "CalculatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ResolutionEvidence_IncidentId",
                table: "ResolutionEvidence",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Assignments");

            migrationBuilder.DropTable(
                name: "CitizenVerifications");

            migrationBuilder.DropTable(
                name: "Correlations");

            migrationBuilder.DropTable(
                name: "History");

            migrationBuilder.DropTable(
                name: "IncidentConfirmations");

            migrationBuilder.DropTable(
                name: "IncidentObservations");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "ObservationDrafts");

            migrationBuilder.DropTable(
                name: "PointsOfInterest");

            migrationBuilder.DropTable(
                name: "PriorityBreakdowns");

            migrationBuilder.DropTable(
                name: "ResolutionEvidence");

            migrationBuilder.DropTable(
                name: "Zones");

            migrationBuilder.DropTable(
                name: "Observations");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
