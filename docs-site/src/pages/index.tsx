import type {ReactNode} from 'react';
import clsx from 'clsx';
import Link from '@docusaurus/Link';
import useBaseUrl from '@docusaurus/useBaseUrl';
import useDocusaurusContext from '@docusaurus/useDocusaurusContext';
import Layout from '@theme/Layout';

import styles from './index.module.css';

const features = [
  {
    title: 'Processing',
    description:
      'The first screen. Every file Weir is working on, from the moment it lands in a watched folder until your media manager has the cleaned copy.',
  },
  {
    title: 'Activity',
    description:
      'Every file Weir has touched: what it was, which tracks it kept and removed, and what came out. When something goes wrong, the reason is here.',
  },
  {
    title: 'Library',
    description:
      'Files already on your storage, checked against your rules. See what would change, and how much space it would free, before anything is touched.',
  },
  {
    title: 'Media managers',
    description:
      'Connect Sonarr, Radarr or Deluno. Weir checks what Sonarr and Radarr are still importing before it touches a file. Deluno hands files to Weir and is told when each one is ready.',
  },
];

const screenshots = [
  {
    title: 'Processing',
    caption:
      'Every file Weir is working on, from the moment it lands to the moment your media manager has it back.',
    src: '/img/processing.png',
    alt: "Weir's Processing screen",
  },
  {
    title: 'Activity',
    caption:
      'Every file Weir has touched: what it was, what Weir did, and what came out.',
    src: '/img/history.png',
    alt: "Weir's Activity screen",
  },
];

function Hero(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  return (
    <header className={clsx('hero', styles.hero)}>
      <div className="container">
        <h1 className="hero__title">{siteConfig.title}</h1>
        <p className="hero__subtitle">{siteConfig.tagline}</p>
        <div className={styles.buttons}>
          <Link className="button button--primary button--lg" to="/docs/quickstart">
            Get Started
          </Link>
          <Link
            className="button button--secondary button--lg"
            to="https://github.com/EJM-Digital/Weir">
            GitHub
          </Link>
        </div>
      </div>
    </header>
  );
}

function Feature({
  title,
  description,
}: {
  title: string;
  description: string;
}): ReactNode {
  return (
    <div className={clsx('col col--3')}>
      <div className={styles.featureCard}>
        <h3>{title}</h3>
        <p>{description}</p>
      </div>
    </div>
  );
}

function Features(): ReactNode {
  return (
    <section className={styles.features}>
      <div className="container">
        <div className="row">
          {features.map((props) => (
            <Feature key={props.title} {...props} />
          ))}
        </div>
      </div>
    </section>
  );
}

function Screenshot({
  title,
  caption,
  src,
  alt,
}: {
  title: string;
  caption: string;
  src: string;
  alt: string;
}): ReactNode {
  return (
    <section className={styles.preview}>
      <div className="container">
        <h2>{title}</h2>
        <p>{caption}</p>
        <img src={useBaseUrl(src)} alt={alt} className={styles.dashboardImage} />
      </div>
    </section>
  );
}

export default function Home(): ReactNode {
  const {siteConfig} = useDocusaurusContext();
  return (
    <Layout description={siteConfig.tagline}>
      <Hero />
      <main>
        <Features />
        {screenshots.map((props) => (
          <Screenshot key={props.title} {...props} />
        ))}
      </main>
    </Layout>
  );
}
