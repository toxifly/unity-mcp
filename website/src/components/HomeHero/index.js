import React from 'react';
import Link from '@docusaurus/Link';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import CopyButton from '@site/src/components/CopyButton';
import styles from './styles.module.css';

const LOCAL_DEPENDENCY = '"com.coplaydev.unity-mcp": "file:/path/to/unity-mcp/MCPForUnity"';

export default function HomeHero() {
  const { siteConfig } = useDocusaurusContext();
  const version = siteConfig.customFields?.latestVersion ?? 'v10.0.0';

  return (
    <header className={styles.hero}>
      <div className={styles.gridBackdrop} aria-hidden="true" />

      <div className={styles.inner}>
        <div className={styles.statusBar}>
          <span className={styles.statusDot} aria-hidden="true" />
          <span className={styles.statusKey}>STATUS</span>
          <span className={styles.statusValue}>OPERATIONAL · {version}</span>
        </div>

        <h1 className={styles.headline}>
          Run the Unity Editor<br />
          with your <em>AI&nbsp;assistant</em>.
        </h1>

        <p className={styles.tagline}>
          MCP for Unity bridges AI assistants — Claude, Codex, VS Code,
          local LLMs, and more — with the Unity Editor via the Model
          Context Protocol. Manage assets, control scenes, edit scripts,
          run tests, automate workflows.
        </p>

        <div className={styles.ctaRow}>
          <Link className={styles.ctaPrimary} to="/getting-started/install">
            Get started
            <span className={styles.ctaArrow} aria-hidden="true">↗</span>
          </Link>
          <Link className={styles.ctaSecondary} to="/reference/tools">
            Browse the reference
            <span className={styles.linkArrow} aria-hidden="true">→</span>
          </Link>
        </div>

        <div className={styles.install}>
          <div className={styles.installHeader}>
            <span className={styles.installLabel}>// INSTALL · local checkout</span>
            <span className={styles.installHint}>
              Add to Packages/manifest.json, or Package Manager → + → Add package from disk
            </span>
          </div>

          <div className={styles.installLine}>
            <span className={styles.installChannel}>FILE</span>
            <code className={styles.installUrl}>{LOCAL_DEPENDENCY}</code>
            <CopyButton text={LOCAL_DEPENDENCY} label="manifest entry" className={styles.installCopy} />
          </div>
        </div>

        <figure className={styles.demo}>
          <figcaption className={styles.demoCaption}>
            <span className={styles.demoTag}>// LIVE</span>
            <span>an MCP client building a scene, end-to-end</span>
          </figcaption>
          <div className={styles.demoFrame}>
            <video
              autoPlay
              loop
              muted
              playsInline
              preload="metadata"
              poster="/unity-mcp/img/logo.png"
              aria-label="An LLM building a Unity scene through MCP for Unity"
              width="640"
              height="416"
            >
              <source src="/unity-mcp/img/building_scene.webm" type="video/webm" />
              <source src="/unity-mcp/img/building_scene.mp4" type="video/mp4" />
              {/* GIF fallback retained for ancient browsers */}
              <img
                src="/unity-mcp/img/building_scene.gif"
                alt="An LLM building a Unity scene through MCP for Unity"
                width="640"
                height="416"
                loading="lazy"
              />
            </video>
          </div>
        </figure>
      </div>
    </header>
  );
}
