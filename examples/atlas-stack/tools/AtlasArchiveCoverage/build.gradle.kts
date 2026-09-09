plugins {
    java
    id("fabric-loom") version "1.17.19"
}

group = "com.b2btatlas.archive"
version = property("mod_version")!!

base {
    archivesName.set("atlas-archive-coverage")
}

repositories {
    mavenCentral()
    maven("https://maven.fabricmc.net/")
}

dependencies {
    minecraft("com.mojang:minecraft:${property("minecraft_version")}")
    mappings(loom.officialMojangMappings())
    modImplementation("net.fabricmc:fabric-loader:${property("fabric_loader_version")}")
    modImplementation("net.fabricmc.fabric-api:fabric-api:${property("fabric_api_version")}")
}

java {
    toolchain.languageVersion.set(JavaLanguageVersion.of(21))
    withSourcesJar()
}

tasks.processResources {
    val tokens = mapOf(
        "version" to project.version.toString(),
        "minecraft_version" to project.property("minecraft_version").toString(),
        "fabric_loader_version" to project.property("fabric_loader_version").toString(),
    )
    inputs.properties(tokens)
    filesMatching("fabric.mod.json") { expand(tokens) }
}

tasks.withType<JavaCompile>().configureEach {
    options.release.set(21)
}

val coverageRegression = tasks.register<JavaExec>("coverageRegression") {
    dependsOn(tasks.testClasses)
    classpath = sourceSets.test.get().runtimeClasspath
    mainClass.set("com.b2btatlas.archive.coverage.CoverageIndexTest")
}
tasks.check { dependsOn(coverageRegression) }
val savedTerrainRegression = tasks.register<JavaExec>("savedTerrainRegression") {
    dependsOn(tasks.testClasses)
    classpath = sourceSets.test.get().runtimeClasspath
    mainClass.set("com.b2btatlas.archive.coverage.SavedTerrainReaderTest")
    if (project.hasProperty("resumeFixture")) args(project.property("resumeFixture").toString())
}
tasks.check { dependsOn(savedTerrainRegression) }
// The dependency-free differential suite runs through coverageRegression.
tasks.test { failOnNoDiscoveredTests.set(false) }

val sparseRepairRegression = tasks.register<JavaExec>("sparseRepairRegression") {
    dependsOn(tasks.testClasses)
    classpath = sourceSets.test.get().runtimeClasspath
    mainClass.set("com.b2btatlas.archive.coverage.SparseRepairPlanTest")
}
tasks.check { dependsOn(sparseRepairRegression) }
